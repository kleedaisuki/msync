//! Physical endpoint identities shared by the compiler, planner and transports.

use std::fmt;

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub enum Backend {
    Local,
    Wsl,
    Ssh {
        host: String,
        user: Option<String>,
        port: Option<u16>,
    },
    Http {
        base: String,
    },
    Path,
    Env,
    Tcp {
        host: String,
        port: u16,
    },
}

#[derive(Clone, Debug, PartialEq, Eq, Hash)]
pub struct Location {
    pub backend: Backend,
    pub path: String,
}

impl fmt::Display for Backend {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Local => f.write_str("$local"),
            Self::Wsl => f.write_str("$wsl"),
            Self::Path => f.write_str("$path"),
            Self::Env => f.write_str("$env"),
            Self::Tcp { host, port } if host.contains(':') && !host.starts_with('[') => {
                write!(f, "tcp://[{host}]:{port}")
            }
            Self::Tcp { host, port } => write!(f, "tcp://{host}:{port}"),
            Self::Http { base } => f.write_str(base),
            Self::Ssh { host, user, port } => {
                f.write_str("ssh://")?;
                if let Some(user) = user {
                    write!(f, "{user}@")?;
                }
                f.write_str(host)?;
                if let Some(port) = port {
                    write!(f, ":{port}")?;
                }
                Ok(())
            }
        }
    }
}

impl fmt::Display for Location {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}::{:?}", self.backend, self.path)
    }
}
