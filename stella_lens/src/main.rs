use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::env;
use std::fs;
use std::path::Path;
use syn::visit::{self, Visit};
use syn::{ItemEnum, ItemFn, ItemStruct, ItemTrait, UseTree};
use walkdir::WalkDir;

#[derive(Serialize, Deserialize, Debug, Default)]
struct ModuleInfo {
    #[serde(default)]
    public_structs: Vec<String>,
    #[serde(default)]
    public_enums: Vec<String>,
    #[serde(default)]
    public_traits: Vec<String>,
    #[serde(default)]
    public_functions: Vec<String>,
    #[serde(default)]
    uses_internal: Vec<String>,
    #[serde(default)]
    module_type: String,
}

#[derive(Serialize, Deserialize, Debug, Default)]
struct ProjectBlueprintJson {
    project_name: String,
    rust_edition: String,
    dependencies: Vec<String>,
    modules_graph: HashMap<String, ModuleInfo>,
}

struct FileAnalyzer {
    project_name: String,
    info: ModuleInfo,
}

impl<'ast> Visit<'ast> for FileAnalyzer {
    fn visit_item_struct(&mut self, node: &'ast ItemStruct) {
        if let syn::Visibility::Public(_) = node.vis {
            self.info.public_structs.push(node.ident.to_string());
        }
        visit::visit_item_struct(self, node);
    }

    fn visit_item_enum(&mut self, node: &'ast ItemEnum) {
        if let syn::Visibility::Public(_) = node.vis {
            self.info.public_enums.push(node.ident.to_string());
        }
        visit::visit_item_enum(self, node);
    }

    fn visit_item_trait(&mut self, node: &'ast ItemTrait) {
        if let syn::Visibility::Public(_) = node.vis {
            self.info.public_traits.push(node.ident.to_string());
        }
        visit::visit_item_trait(self, node);
    }

    fn visit_item_fn(&mut self, node: &'ast ItemFn) {
        if let syn::Visibility::Public(_) = node.vis {
            self.info.public_functions.push(node.sig.ident.to_string());
        }
        visit::visit_item_fn(self, node);
    }

    fn visit_use_tree(&mut self, node: &'ast UseTree) {
        let mut path_segments = Vec::new();
        
        fn extract_root(tree: &UseTree, segments: &mut Vec<String>) {
            match tree {
                UseTree::Path(p) => {
                    segments.push(p.ident.to_string());
                    extract_root(&*p.tree, segments);
                }
                UseTree::Name(n) => {
                    segments.push(n.ident.to_string());
                }
                UseTree::Group(g) => {
                    if let Some(first) = g.items.iter().next() {
                        extract_root(first, segments);
                    }
                }
                _ => {}
            }
        }

        extract_root(node, &mut path_segments);

        if let Some(root_import) = path_segments.first() {
            if root_import == "crate" || root_import == "self" || root_import == &self.project_name {
                let full_import_path = path_segments.join("::");
                if !self.info.uses_internal.contains(&full_import_path) {
                    self.info.uses_internal.push(full_import_path);
                }
            }
        }

        visit::visit_use_tree(self, node);
    }
}

fn main() {
    let args: Vec<String> = env::args().collect();
    if args.len() < 2 {
        eprintln!("Error: Target project path required.");
        std::process::exit(1);
    }

    let project_path = Path::new(&args[1]); 
    let cargo_toml_path = project_path.join("Cargo.toml");

    if !cargo_toml_path.exists() {
        eprintln!("Error: Cargo.toml not found at {:?}", cargo_toml_path);
        std::process::exit(1);
    }

    let manifest = match cargo_toml::Manifest::from_path(&cargo_toml_path) {
        Ok(m) => m,
        Err(e) => {
            eprintln!("Error parsing Cargo.toml: {}", e);
            std::process::exit(1);
        }
    };

    let package = manifest.package.expect("No [package] found in Cargo.toml");
        let project_name = package.name;
        
        let edition_enum = match package.edition {
                cargo_toml::Inheritable::Set(e) => e,
                cargo_toml::Inheritable::Inherited { .. } => cargo_toml::Edition::E2021,
            };
        
            let rust_edition = match edition_enum {
                cargo_toml::Edition::E2015 => "2015",
                cargo_toml::Edition::E2018 => "2018",
                cargo_toml::Edition::E2021 => "2021",
                _ => "2021", 
            }.to_string();
    
        let mut dependencies = Vec::new();
    for dep_name in manifest.dependencies.keys() {
        dependencies.push(dep_name.clone());
    }

    let mut modules_graph = HashMap::new();
    let src_path = project_path.join("src");

    if src_path.exists() {
        for entry in WalkDir::new(&src_path).into_iter().filter_map(|e| e.ok()) {
            let path = entry.path();
            if path.extension().map_or(false, |ext| ext == "rs") {
                if let Ok(content) = fs::read_to_string(path) {
                    if let Ok(ast) = syn::parse_file(&content) {
                        let mut analyzer = FileAnalyzer {
                            project_name: project_name.clone(),
                            info: ModuleInfo::default(),
                        };

                      
                        analyzer.visit_file(&ast);

                        let file_name = path.file_name().unwrap().to_string_lossy();
                        analyzer.info.module_type = if file_name == "main.rs" {
                            "BinaryRoot".to_string()
                        } else if file_name == "lib.rs" {
                            "LibraryRoot".to_string()
                        } else {
                            "NormalModule".to_string()
                        };

                        if let Ok(rel_path) = path.strip_prefix(project_path) {
                            modules_graph.insert(rel_path.to_string_lossy().to_string(), analyzer.info);
                        }
                    }
                }
            }
        }
    }

    let blueprint = ProjectBlueprintJson {
        project_name,
        rust_edition,
        dependencies,
        modules_graph,
    };

    
    if let Ok(json_output) = serde_json::to_string_pretty(&blueprint) {
        println!("{}", json_output);
    }
}