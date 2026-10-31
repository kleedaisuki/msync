//! Release-only paired file-to-TCP capability probe; not a CLI latency benchmark.

#[cfg(target_os = "linux")]
mod linux {
    use std::fs::File;
    use std::io::{self, Seek};
    use std::path::Path;
    use std::time::Instant;

    use serde::Serialize;
    use tokio::io::{AsyncReadExt, AsyncWriteExt};
    use tokio::net::{TcpListener, TcpStream};
    use tokio_util::sync::CancellationToken;

    #[derive(Clone, Copy, Debug, Serialize)]
    #[serde(rename_all = "snake_case")]
    enum Mechanism {
        Kernel,
        Buffered,
        Dispatch,
    }

    #[derive(Debug, Serialize)]
    struct Sample {
        size: usize,
        pair: usize,
        position: usize,
        mechanism: Mechanism,
        elapsed_us: f64,
        cpu_us: f64,
    }

    fn cpu_us() -> io::Result<f64> {
        let mut time = libc::timespec {
            tv_sec: 0,
            tv_nsec: 0,
        };
        // SAFETY: The valid output record is exclusively borrowed for this call.
        if unsafe { libc::clock_gettime(libc::CLOCK_PROCESS_CPUTIME_ID, &mut time) } != 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(time.tv_sec as f64 * 1_000_000.0 + time.tv_nsec as f64 / 1_000.0)
    }

    async fn trial(
        path: &Path,
        size: usize,
        mechanism: Mechanism,
        pair: usize,
        position: usize,
    ) -> io::Result<Sample> {
        let mut source = File::open(path)?;
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).await?;
        let mut socket = TcpStream::connect(listener.local_addr()?).await?;
        let (mut receiver, _) = listener.accept().await?;
        let received = Vec::with_capacity(size + 1);
        let read = tokio::spawn(async move {
            let mut received = received;
            receiver.read_to_end(&mut received).await?;
            Ok::<_, io::Error>(received)
        });
        let cpu = cpu_us()?;
        let start = Instant::now();
        match mechanism {
            Mechanism::Kernel => {
                let result =
                    msync::io::file_to_tcp(&source, &socket, 64 * 1024, &CancellationToken::new())
                        .await?;
                if result.end != Some(msync::io::FlowEnd::SourceClosed)
                    || result.bytes != size as u64
                {
                    return Err(io::Error::other(format!(
                        "kernel capability declined or copied an unexpected prefix: {result:?}"
                    )));
                }
                if source.stream_position()? != 0 {
                    return Err(io::Error::other(
                        "kernel capability moved the borrowed source cursor",
                    ));
                }
            }
            Mechanism::Buffered => {
                let mut source = tokio::fs::File::from_std(source);
                source.set_max_buf_size(64 * 1024);
                let mut buffer = vec![0; 64 * 1024];
                loop {
                    let count = source.read(&mut buffer).await?;
                    if count == 0 {
                        break;
                    }
                    socket.write_all(&buffer[..count]).await?;
                }
                socket.shutdown().await?;
            }
            Mechanism::Dispatch => unreachable!("production dispatch has a separate topology"),
        }
        let received = read.await.map_err(io::Error::other)??;
        let elapsed_us = start.elapsed().as_secs_f64() * 1_000_000.0;
        let cpu_us = cpu_us()? - cpu;
        if received.len() != size {
            return Err(io::Error::other(format!(
                "receiver got {} bytes, expected {size}",
                received.len()
            )));
        }
        for (index, &byte) in received.iter().enumerate() {
            if byte != payload_byte(index) {
                return Err(io::Error::other(format!(
                    "receiver byte mismatch at {index}"
                )));
            }
        }
        Ok(Sample {
            size,
            pair,
            position,
            mechanism,
            elapsed_us,
            cpu_us,
        })
    }

    async fn dispatch_trial(path: &Path, size: usize, pair: usize) -> io::Result<Sample> {
        let listener = TcpListener::bind((std::net::Ipv4Addr::LOCALHOST, 0)).await?;
        let address = listener.local_addr()?;
        let operation = msync::compiler::CopyOp {
            source: msync::endpoint::Location {
                backend: msync::endpoint::Backend::Local,
                path: path
                    .to_str()
                    .ok_or_else(|| io::Error::other("non-UTF-8 fixture"))?
                    .to_owned(),
            },
            destination: msync::endpoint::Location {
                backend: msync::endpoint::Backend::Tcp {
                    host: address.ip().to_string(),
                    port: address.port(),
                },
                path: "/".into(),
            },
            pattern: None,
            span: msync::syntax::Span::default(),
        };
        let received = Vec::with_capacity(size + 1);
        let read = tokio::spawn(async move {
            let (mut receiver, _) = listener.accept().await?;
            let mut received = received;
            receiver.read_to_end(&mut received).await?;
            Ok::<_, io::Error>(received)
        });
        let cpu = cpu_us()?;
        let start = Instant::now();
        let result = msync::streams::transfer(&operation, 64 * 1024, CancellationToken::new())
            .await
            .map_err(io::Error::other)?;
        let received = read.await.map_err(io::Error::other)??;
        let elapsed_us = start.elapsed().as_secs_f64() * 1_000_000.0;
        let cpu_us = cpu_us()? - cpu;
        if result.end != msync::io::FlowEnd::SourceClosed || result.bytes != size as u64 {
            return Err(io::Error::other(format!(
                "production dispatch copied an unexpected prefix: {result:?}"
            )));
        }
        if received.len() != size
            || received
                .iter()
                .enumerate()
                .any(|(index, &byte)| byte != payload_byte(index))
        {
            return Err(io::Error::other("production receiver payload mismatch"));
        }
        Ok(Sample {
            size,
            pair,
            position: 0,
            mechanism: Mechanism::Dispatch,
            elapsed_us,
            cpu_us,
        })
    }

    fn payload_byte(index: usize) -> u8 {
        let block = index / 4096;
        let shift = (index % 8) * 8;
        let block_byte = ((block as u64).rotate_left(17) >> shift) as u8;
        block_byte ^ (index % 251) as u8
    }

    #[test]
    #[ignore = "host timing probe; run optimized and without concurrent builds"]
    fn file_tcp_crossover() -> Result<(), Box<dyn std::error::Error>> {
        if cfg!(debug_assertions) {
            return Err("this probe requires a release build".into());
        }
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/io-v6");
        std::fs::create_dir_all(&root)?;
        let fixture = tempfile::tempdir_in(root)?;
        let samples: usize = std::env::var("MSYNC_IO_V6_SAMPLES")
            .unwrap_or_else(|_| "31".into())
            .parse()?;
        let sizes: Vec<usize> = std::env::var("MSYNC_IO_V6_SIZES")
            .unwrap_or_else(|_| "4096,16384,65536,262144,1048576,16777216".into())
            .split(',')
            .map(str::parse)
            .collect::<Result<_, _>>()?;
        if samples == 0 || sizes.contains(&0) {
            return Err("sample count and payload sizes must be positive".into());
        }
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        runtime.block_on(async {
            for size in sizes {
                let path = fixture.path().join(format!("payload-{size}"));
                std::fs::write(&path, (0..size).map(payload_byte).collect::<Vec<_>>())?;
                // Both arms share the same warm source. Socket topology and file
                // opening are outside timing; EOF reception is inside timing.
                for pair in 0..3 {
                    for mechanism in [Mechanism::Kernel, Mechanism::Buffered] {
                        trial(&path, size, mechanism, pair, 0).await?;
                    }
                }
                for pair in 0..samples {
                    let order = if pair % 2 == 0 {
                        [Mechanism::Kernel, Mechanism::Buffered]
                    } else {
                        [Mechanism::Buffered, Mechanism::Kernel]
                    };
                    for (position, mechanism) in order.into_iter().enumerate() {
                        let sample = trial(&path, size, mechanism, pair, position).await?;
                        println!("{}", serde_json::to_string(&sample)?);
                    }
                }
            }
            Ok::<_, Box<dyn std::error::Error>>(())
        })
    }

    #[test]
    #[ignore = "host timing probe; compare frozen before/after release libraries"]
    fn file_tcp_production_dispatch() -> Result<(), Box<dyn std::error::Error>> {
        if cfg!(debug_assertions) {
            return Err("this probe requires a release build".into());
        }
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp/io-v6");
        std::fs::create_dir_all(&root)?;
        let fixture = tempfile::tempdir_in(root)?;
        let samples: usize = std::env::var("MSYNC_IO_V6_SAMPLES")
            .unwrap_or_else(|_| "31".into())
            .parse()?;
        let sizes: Vec<usize> = std::env::var("MSYNC_IO_V6_SIZES")
            .unwrap_or_else(|_| "4096,65536,131072,1048576".into())
            .split(',')
            .map(str::parse)
            .collect::<Result<_, _>>()?;
        if samples == 0 || sizes.contains(&0) {
            return Err("sample count and payload sizes must be positive".into());
        }
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()?;
        runtime.block_on(async {
            for size in sizes {
                let path = fixture.path().join(format!("payload-{size}"));
                std::fs::write(&path, (0..size).map(payload_byte).collect::<Vec<_>>())?;
                for pair in 0..3 {
                    dispatch_trial(&path, size, pair).await?;
                }
                for pair in 0..samples {
                    println!(
                        "{}",
                        serde_json::to_string(&dispatch_trial(&path, size, pair).await?)?
                    );
                }
            }
            Ok::<_, Box<dyn std::error::Error>>(())
        })
    }
}
