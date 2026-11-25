# verbose-pkg (`vpm`)

A **multithreaded, serverless, Cargo-inspired package manager** for **[VerBose-Lang](https://github.com/thetrung/verbose-lang)** (`v0.7.1+`) — written natively in 100% pure VerBose-Lang using `Async`/`Await` OS threads with zero external server or scripting runtime dependencies.

## Highlights

- **Multithreaded Parallel Execution (`-j N` / `--jobs N` / `$VPM_JOBS`)**:
  - Defaults automatically to your CPU core count (`sysconf(_SC_NPROCESSORS_ONLN)`).
  - **`vpm install [-j N]`**: Fetches (`git`, `path`, `.vpkg.tar.gz`), stages `.vb` headers, and builds all dependencies **in parallel** across OS threads while keeping `.vpkg/flags.txt` and `verbose.lock` in deterministic manifest order.
  - **`vpm build [-j N]`**: Builds all `[workspace]` member libraries/apps **in parallel** (and compiles multiple `c_sources` in parallel).
  - **`vpm test [-j N] [--deps]`**: Discovers and runs every test suite (`test_*.vb` / `*_test.vb`), `[workspace]` member library, and optionally every installed dependency library (`--deps`) **in parallel** with non-interleaved per-suite logs and timing.
- **Serverless Dependency Resolution**:
  1. **Git repositories** (`git = "https://github.com/owner/repo"` or GitHub shorthand `vpm add verbose-raylib thetrung/verbose-raylib`)
  2. **Local directories** (`path = "../verbose-raylib"`)
  3. **Portable source or prebuilt binary tarballs** (`.vpkg.tar.gz` — local file or any HTTPS URL / GitHub Release asset)

## Example `verbose.toml`

### Single Project
```toml
[package]
name = "my-raylib-game"
version = "0.1.0"
entry = "src/main.vb"

[dependencies]
verbose-raylib = { git = "https://github.com/thetrung/verbose-raylib", branch = "main" }
```

### Multi-Library Workspace (Parallel Build & Test)
```toml
[package]
name = "my-workspace"
version = "0.1.0"

[workspace]
members = ["lib-math", "lib-net", "app"]
```

## Quick Start

```bash
# Build & install vpm
make build
sudo make install

# Install all dependencies in parallel (defaults to CPU count)
vpm install -j 8

# Build & test all libraries / test suites in parallel
vpm build -j 8
vpm test -j 8 --deps

# Pack your own library for distribution
vpm pack           # -> dist/<name>-<version>.vpkg.tar.gz
vpm pack --binary  # -> dist/<name>-<version>-bin.vpkg.tar.gz (includes lib<name>.a)
```

## Commands

| Command | Description |
| :--- | :--- |
| `vpm init [name]` | Scaffold a new VerBose project (`verbose.toml`, `src/main.vb`, `.gitignore`) |
| `vpm add <name> <source>` | Add a git URL, `owner/repo`, local path, or `.vpkg.tar.gz` dependency and install |
| `vpm install [-j N]` | Fetch & build dependencies in parallel, stage `.vpkg/include`, write `verbose.lock` |
| `vpm build [-j N] [args...]` | Build `[package].entry` (or all `[workspace].members` in parallel) |
| `vpm run [args...]` | Build and run `[package].entry` with all resolved dependency flags |
| `vpm test [-j N] [--deps] [target]` | Run test suites, `[workspace]` members, and optional `--deps` libraries in parallel |
| `vpm pack [--binary] [-j N]` | Pack current library into `dist/<name>-<version>[-bin].vpkg.tar.gz` |
| `vpm list` | Show package/workspace metadata and resolved dependencies |
| `vpm clean` | Remove `.vpkg/` and `build/` |
