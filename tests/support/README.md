# Shared real-process fixtures

`ssh_fixture.py` owns a loopback OpenSSH daemon, temporary host/client keys,
native alias configuration and client-count wrappers under the caller's
repository-local `.temp` directory. It never edits host SSH configuration and
always closes its owned daemon. Transfer, completion, authentication and standard
library journeys share this fixture.

The fixture was extracted from the retired Rust benchmark infrastructure so
current .NET validation has no dependency on a Rust checkout or toolchain.
It is development infrastructure, not a product runtime dependency.
