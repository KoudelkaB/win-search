fn main() {
    println!("cargo:rerun-if-changed=assets/logexplorer.rc");
    println!("cargo:rerun-if-changed=assets/logexplorer.ico");
    embed_resource::compile_for("assets/logexplorer.rc", ["logexplorer"], embed_resource::NONE).manifest_optional().expect("embed the Log Explorer icon");
}
