# verbose-pkg (`vpm`)

A **serverless, Cargo-inspired package manager** for **[VerBose-Lang](https://github.com/thetrung/verbose-lang)** (`v0.6.0+`) — written natively in VerBose-Lang itself with zero external server or scripting runtime dependencies.

Designed directly from the lessons of packaging mixed VerBose + C ABI libraries like [`verbose-raylib`](https://github.com/thetrung/verbose-raylib) and [`verbose-tui`](https://github.com/thetrung/verbose-tui).

## How It Works (Serverless)

Instead of requiring a central registry server, **`vpm`** resolves dependencies directly from:
1. **Git repositories** (`git = "https://github.com/owner/repo"` or GitHub shorthand `vpm add raylib thetrung/verbose-raylib`)
2. **Local directories** (`path = "../verbose-raylib"`)
3. **Portable source or prebuilt binary tarballs** (`.vpkg.tar.gz` — local file or any HTTPS URL / GitHub Release asset!)

When `vpm install` runs, it:
- Stages every dependency's `.vb` headers into `.vpkg/include/<dep>/` (automatically discovered by `verbose` and `verbose.nvim`!)
- Compiles each dependency's C shim sources (`c_sources`) into a static archive `.vpkg/lib/lib<dep>.a` — or links a prebuilt `.a` directly if packed with `vpm pack --binary`!
- Collects all `-I`, `.a`, and `-l<lib>` flags into `.vpkg/flags.txt` and generates `verbose.lock`.
- `vpm build`, `vpm run`, and `vpm test` forward all flags automatically — **no more manual `-I` / `.c` / `-l` flags!**

## Example `verbose.toml`

```toml
[package]
name = "my-raylib-game"
version = "0.1.0"
entry = "src/main.vb"

[dependencies]
verbose-raylib = { git = "https://github.com/thetrung/verbose-raylib", branch = "main" }
```

## Quick Start

```bash
# Build & install vpm
make build
sudo make install

# Scaffold a new project & add verbose-raylib
vpm init my-game
cd my-game
vpm add verbose-raylib thetrung/verbose-raylib

# Build & run with zero manual compiler flags
vpm run

# Pack your own library for distribution (source or prebuilt binary bundle)
vpm pack           # -> dist/<name>-<version>.vpkg.tar.gz
vpm pack --binary  # -> dist/<name>-<version>-bin.vpkg.tar.gz (includes lib<name>.a)
```

## Commands

| Command | Description |
| :--- | :--- |
| `vpm init [name]` | Scaffold a new VerBose project (`verbose.toml`, `src/main.vb`, `.gitignore`) |
| `vpm add <name> <source>` | Add a git URL, `owner/repo`, local path, or `.vpkg.tar.gz` dependency and install |
| `vpm install` | Fetch dependencies, compile/stage `.a` archives & `.vpkg/include`, write `verbose.lock` |
| `vpm build [args...]` | Build `[package].entry` -> `build/<name>` with all resolved dependency flags |
| `vpm run [args...]` | Build and run `[package].entry` with all resolved dependency flags |
| `vpm test [target]` | Run `verbose test` with all resolved dependency flags |
| `vpm pack [--binary]` | Pack current library into `dist/<name>-<version>[-bin].vpkg.tar.gz` |
| `vpm list` | Show package metadata and resolved dependencies |
| `vpm clean` | Remove `.vpkg/` and `build/` |
