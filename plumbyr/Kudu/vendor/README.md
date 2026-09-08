# Retained Kudu source

The two TypeScript modules and browsers.json are copied unchanged from Kudu 2.8.0 (https://github.com/adventdevinc/kudu). Only their Node-compatible browser discovery and path resolution functions run here. The relative type-only imports are erased by Node; the rest of Kudu is not required.

Keep LICENSE with these files. Node and its bundled dependency notices come from the checksum-verified official archive selected in scripts/node-runtime.json and are embedded in the application as well.
