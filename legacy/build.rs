fn main() {
    println!("cargo:rerun-if-changed=build.rs");
    let windows = std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows");
    let msvc = std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc");
    if !windows || !msvc {
        return;
    }

    // The editor's clipboard imports otherwise initialize USER32 for every CLI
    // command (~2 ms here), even --version. Let the platform load it on use.
    // https://learn.microsoft.com/cpp/build/reference/delayload-delay-load-import
    println!("cargo:rustc-link-arg-bin=msync=/DELAYLOAD:USER32.dll");
    println!("cargo:rustc-link-arg-bin=msync=delayimp.lib");
}
