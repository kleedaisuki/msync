//! Bounded byte frames; paths retain native UTF-16 rather than lossy JSON text.

use std::ffi::{OsStr, OsString};
use std::io;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::{Component, Path, PathBuf};
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite, AsyncWriteExt};

pub(super) const HELLO: u8 = 1;
pub(super) const SPEC: u8 = 2;
pub(super) const ROOT: u8 = 3;
pub(super) const DIRECTORY: u8 = 4;
pub(super) const FILE: u8 = 5;
pub(super) const DATA: u8 = 6;
pub(super) const END_ROOT: u8 = 7;
pub(super) const SEEDED: u8 = 8;
pub(super) const STARTED: u8 = 9;
pub(super) const STDIN: u8 = 10;
pub(super) const CLOSE_STDIN: u8 = 11;
pub(super) const STDOUT: u8 = 12;
pub(super) const STDERR: u8 = 13;
pub(super) const EXIT: u8 = 14;
pub(super) const EXPORT: u8 = 15;
pub(super) const COMPLETE: u8 = 16;
pub(super) const CANCEL: u8 = 17;
pub(super) const FAILURE: u8 = 18;
const MAX_FRAME: usize = 1024 * 1024;

pub(super) fn hello(nonce: &str) -> Vec<u8> {
    format!("msync-windows-view:2:{nonce}").into_bytes()
}

pub(super) async fn send(
    sink: &mut (impl AsyncWrite + Unpin),
    kind: u8,
    bytes: &[u8],
) -> io::Result<()> {
    if bytes.len() > MAX_FRAME {
        return Err(invalid("control frame exceeds one MiB"));
    }
    sink.write_u8(kind).await?;
    sink.write_u32_le(bytes.len() as u32).await?;
    sink.write_all(bytes).await?;
    sink.flush().await
}

pub(super) async fn receive(source: &mut (impl AsyncRead + Unpin)) -> io::Result<(u8, Vec<u8>)> {
    let kind = source.read_u8().await?;
    let length = source.read_u32_le().await? as usize;
    if length > MAX_FRAME {
        return Err(invalid("control frame exceeds one MiB"));
    }
    let mut bytes = vec![0; length];
    source.read_exact(&mut bytes).await?;
    Ok((kind, bytes))
}

pub(super) fn invalid(message: &'static str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

#[derive(Default)]
pub(super) struct Encode(pub Vec<u8>);

impl Encode {
    pub fn modified(&mut self, value: SystemTime) {
        let (before, duration) = match value.duration_since(UNIX_EPOCH) {
            Ok(duration) => (false, duration),
            Err(error) => (true, error.duration()),
        };
        self.integer(u64::from(before));
        self.integer(duration.as_secs());
        self.integer(u64::from(duration.subsec_nanos()));
    }
    pub fn integer(&mut self, value: u64) {
        self.0.extend_from_slice(&value.to_le_bytes());
    }

    pub fn text(&mut self, value: &OsStr) -> io::Result<()> {
        let units = value.encode_wide().collect::<Vec<_>>();
        if units.len() > 32767 || units.contains(&0) {
            return Err(invalid(
                "native argument exceeds UTF-16 limits or contains NUL",
            ));
        }
        self.integer(units.len() as u64);
        for unit in units {
            self.0.extend_from_slice(&unit.to_le_bytes());
        }
        Ok(())
    }
}

pub(super) struct Decode<'a>(&'a [u8]);

impl<'a> Decode<'a> {
    pub fn modified(&mut self) -> io::Result<SystemTime> {
        let before = self.integer()?;
        let seconds = self.integer()?;
        let nanos = self.integer()?;
        if before > 1 || nanos >= 1_000_000_000 {
            return Err(invalid("invalid modification timestamp"));
        }
        let duration = Duration::new(seconds, nanos as u32);
        let value = if before == 1 {
            UNIX_EPOCH.checked_sub(duration)
        } else {
            UNIX_EPOCH.checked_add(duration)
        };
        value.ok_or_else(|| invalid("modification timestamp exceeds platform limits"))
    }
    pub fn new(bytes: &'a [u8]) -> Self {
        Self(bytes)
    }

    fn take(&mut self, length: usize) -> io::Result<&'a [u8]> {
        if length > self.0.len() {
            return Err(invalid("truncated control message"));
        }
        let (result, rest) = self.0.split_at(length);
        self.0 = rest;
        Ok(result)
    }

    pub fn integer(&mut self) -> io::Result<u64> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().unwrap()))
    }

    pub fn text(&mut self) -> io::Result<OsString> {
        let length = self.integer()?;
        if length > 32767 {
            return Err(invalid("native argument exceeds UTF-16 limits"));
        }
        let bytes = self.take(length as usize * 2)?;
        let units = bytes
            .as_chunks::<2>()
            .0
            .iter()
            .map(|part| u16::from_le_bytes([part[0], part[1]]))
            .collect::<Vec<_>>();
        if units.contains(&0) {
            return Err(invalid("native argument contains NUL"));
        }
        Ok(OsString::from_wide(&units))
    }

    pub fn path(&mut self) -> io::Result<PathBuf> {
        Ok(PathBuf::from(self.text()?))
    }

    pub fn finish(self) -> io::Result<()> {
        if !self.0.is_empty() {
            return Err(invalid("unexpected trailing control bytes"));
        }
        Ok(())
    }
}

pub(super) fn relative(path: &Path) -> io::Result<()> {
    if path
        .components()
        .any(|part| !matches!(part, Component::Normal(_)))
    {
        return Err(invalid(
            "export entry must contain only normal relative components",
        ));
    }
    if path
        .as_os_str()
        .encode_wide()
        .any(|unit| unit == b':' as u16 || unit == 0)
    {
        return Err(invalid(
            "tree entries cannot contain alternate streams or NUL",
        ));
    }
    Ok(())
}

pub(super) fn joined(root: &Path, relative: &Path) -> PathBuf {
    if relative.as_os_str().is_empty() {
        root.to_owned()
    } else {
        root.join(relative)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn modification_times_roundtrip_across_the_unix_epoch_without_loss() {
        for time in [
            UNIX_EPOCH,
            UNIX_EPOCH + Duration::new(123, 456_700),
            UNIX_EPOCH - Duration::new(123, 456_700),
        ] {
            let mut encoded = Encode::default();
            encoded.modified(time);
            let mut decoded = Decode::new(&encoded.0);
            assert_eq!(decoded.modified().unwrap(), time);
            decoded.finish().unwrap();
        }
        let mut invalid_nanos = Encode::default();
        for value in [0, 0, 1_000_000_000] {
            invalid_nanos.integer(value);
        }
        assert!(Decode::new(&invalid_nanos.0).modified().is_err());
    }

    #[test]
    fn native_utf16_roundtrips_without_lossy_path_conversion() {
        for original in [
            OsString::from("可莉🌟 引号\"\\"),
            OsString::from_wide(&[0xd800, b'x' as u16]),
            OsString::new(),
        ] {
            let mut encoded = Encode::default();
            encoded.text(&original).unwrap();
            let mut decoded = Decode::new(&encoded.0);
            assert_eq!(decoded.text().unwrap(), original);
            decoded.finish().unwrap();
        }
    }

    #[test]
    fn malformed_strings_and_traversal_never_become_filesystem_operations() {
        let mut oversized = Encode::default();
        oversized.integer(u64::MAX);
        assert!(Decode::new(&oversized.0).text().is_err());
        for path in [
            r"..\host",
            r"C:\outside",
            r"\absolute",
            r"a\..\outside",
            r"a:stream",
            r"a\b:stream",
        ] {
            assert!(relative(Path::new(path)).is_err(), "{path}");
        }
        assert!(relative(Path::new("")).is_ok());
        assert!(relative(Path::new("子目录/文件")).is_ok());
    }

    #[tokio::test]
    async fn frame_limits_are_checked_before_allocating_payloads() {
        let (mut writer, mut reader) = tokio::io::duplex(16);
        writer.write_u8(DATA).await.unwrap();
        writer.write_u32_le(u32::MAX).await.unwrap();
        assert_eq!(
            receive(&mut reader).await.unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );
    }
}
