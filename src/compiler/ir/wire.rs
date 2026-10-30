//! Version-1 IR predates the tagged physical endpoint schema. Keep its bytes stable.

use crate::endpoint::{Backend, Location};
use serde::{Deserialize, Deserializer, Serialize, Serializer};

#[derive(Serialize, Deserialize)]
#[serde(remote = "Backend")]
enum LegacyBackend {
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

pub(crate) mod location {
    use super::*;

    #[derive(Serialize, Deserialize)]
    #[serde(remote = "Location")]
    struct LegacyLocation {
        #[serde(with = "LegacyBackend")]
        backend: Backend,
        path: String,
    }

    pub fn serialize<S: Serializer>(value: &Location, serializer: S) -> Result<S::Ok, S::Error> {
        LegacyLocation::serialize(value, serializer)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Location, D::Error> {
        LegacyLocation::deserialize(deserializer)
    }
}

pub(super) mod optional_backend {
    use super::*;

    #[derive(Serialize, Deserialize)]
    struct Wrapper(#[serde(with = "LegacyBackend")] Backend);

    pub fn serialize<S: Serializer>(
        value: &Option<Backend>,
        serializer: S,
    ) -> Result<S::Ok, S::Error> {
        value.clone().map(Wrapper).serialize(serializer)
    }

    pub fn deserialize<'de, D: Deserializer<'de>>(
        deserializer: D,
    ) -> Result<Option<Backend>, D::Error> {
        Option::<Wrapper>::deserialize(deserializer).map(|value| value.map(|wrapped| wrapped.0))
    }
}
