# ContractGuard

A CI gate for .NET API surfaces. An architect prescribes the method signatures a team (or an
AI coding agent) must honor; developers implement the bodies however they like; the build
fails if any prescribed signature drifts.

Contracts are data, not code: decomposed signature elements in a JSON file that lives in the
repo, validated by a schema, reviewed like any other change. Put the contract under
CODEOWNERS and the architect-approval workflow comes for free — and because the gate reads
the *built assembly's metadata* (no code execution, no analyzers a developer can switch
off), it verifies the artifact that actually ships.

New to it? Start with the [owner's manual](docs/owners-manual.md) — quickstart, the
settings in plain language, and what the diagnostic IDs mean.

## Status

Core engine, CLI, and MSBuild gate work end to end. The Roslyn analyzer exists as a
second front-end over the same comparison engine — same CG diagnostic ids, contract via
AdditionalFiles, errors on the offending source line — and a consistency harness asserts
the symbol and metadata front-ends produce identical models, so the analyzer and the gate
cannot disagree. Analyzer packaging is the next step. See
[What the gate can and can't see](#what-the-gate-can-and-cant-see) for the enforcement
boundary.

## How it works

```
samples/MyCompany.Orders.contract.json   what a contract looks like
schema/contractguard.schema.json         draft-07 JSON Schema ($schema gives editor red squiggles)
```

A contract names one assembly, the types it governs, and the members each type must expose:

```json
{
  "$schema": "https://raw.githubusercontent.com/lxman/ContractGuard/main/schema/contractguard.schema.json",
  "assembly": "Shop.Domain",
  "types": [
    {
      "type": "Shop.Calc",
      "kind": "class",
      "members": [
        { "kind": "method", "name": "Add", "returns": "int",
          "params": [["int", "a"], ["int", "b"]] }
      ]
    }
  ]
}
```

Policy lives *inside* the contract (`settings`: exact-vs-open surface, parameter-name
significance, accessibility scope...) so strictness cannot be weakened from a CI flag —
changing policy means changing the reviewed file.

## Use it

**CLI** (`dotnet tool install -g ContractGuard --prerelease`):

```
contractguard extract   --assembly Shop.Domain.dll --output Shop.Domain.contract.json
contractguard verify    --contract Shop.Domain.contract.json --assembly Shop.Domain.dll
contractguard show      --contract Shop.Domain.contract.json
contractguard add       --contract Shop.Domain.contract.json --type OrderService "public Task<Result> Submit(Order order)"
contractguard import    --contract Shop.Domain.contract.json IOrderContract.cs
contractguard normalize --contract Shop.Domain.contract.json --check
```

`extract` takes `--scope public,protected,internal,private` to pull out more than the
default public+protected surface — prescribed members of any accessibility are enforced
either way; scope governs what the deny sweeps and extraction consider surface.

`extract` bootstraps a contract from a golden build; `verify` is the gate (exit 0 pass,
1 violations, 2 errors); `show` renders the elements back as C# declarations. The
authoring verbs go the other way: `add` decomposes a C# declaration string into elements
(the contract file never stores C# text), `import` decomposes a whole scaffold file - an
interface control document the architect wrote - and `normalize` rewrites a contract to
canonical form (`--check` makes it a CI lint).

**MSBuild package** (the drop-in):

```xml
<PackageReference Include="ContractGuard.MSBuild" Version="0.0.4-alpha" PrivateAssets="all" />
```

After every build, `<project>/<AssemblyName>.contract.json` is verified automatically —
violations land in the IDE error list pointing at the contract file. Projects without a
contract file are skipped, so the reference can live solution-wide in Directory.Build.props.
In CI, build with `-p:ContractGuardRequireContract=true` so a deleted contract file fails
the build instead of silently removing the gate.

## What the gate can and can't see

The gate reads assembly metadata, so its enforcement boundary is metadata's boundary.

### Identical in metadata — by design, permanent

- **`= default` on a struct parameter and `= null` on a reference parameter compile to the
  same constant** (a nullref). The gate therefore treats the JSON forms `"default": null`
  and `{"$special": "default"}` as interchangeable, and `extract` emits `null` for both.
  This can never produce a false pass: a given parameter type only admits one of the two
  meanings.
- **`async` does not exist in a binary signature.** It is an implementation detail an
  implementer may freely add or remove, which is why it is deliberately absent from the
  contract vocabulary. Prescribe `Task<T> Submit(...)`; whether the body is `async` is not
  the architect's business.

### Decoded from attribute metadata

- **Nullable reference annotations** (`NullableAttribute`/`NullableContextAttribute`) are
  decoded, so `nullableAnnotations: significant` enforces `string` vs `string?` for real.
  The default stays `ignored` — oblivious (pre-nullable) assemblies carry no annotations,
  and a mixed-context shop turning this on should do so deliberately. `int?` is a real
  type, `Nullable<int>`, and is always enforced regardless.
- **Tuple element names** (`TupleElementNamesAttribute`) are decoded and significant by
  default — renaming `(int x, int y)` to `(int a, int b)` breaks consumers using named
  access.
- **Record classes** are detected (the `EqualityContract` compiler pattern) and compare as
  `"kind": "record"`; the synthesized plumbing (`EqualityContract`, `PrintMembers`) is not
  governable surface, while public synthesized members (`Equals`, operators, `Deconstruct`)
  are. Record *structs* have no metadata marker and stay `struct`.
- **`ref readonly`** returns and parameters and **`volatile`** fields decode from their
  modreqs/attributes.
- **Constraint and inheritance nullability** — `notnull`, `class?`, `where T : IFoo?`, and
  annotations on base types and implemented interfaces — decode when `nullableAnnotations`
  is significant.
- **Enum parameter defaults** written as `"OrderStatus.Pending"` resolve against enums
  defined in the scanned assembly. Enums from *other* assemblies still need the underlying
  numeric value — the gate never loads foreign assemblies.
- **Explicit interface implementations** are governed via `explicitInterface` on a member
  (`{ "kind": "method", "name": "Dispose", "explicitInterface": "IDisposable", ... }`);
  an implicit implementation never satisfies a prescribed explicit one, and vice versa.
- **`significantAttributes`** is enforced: within the listed attribute universe, presence
  must match the prescription exactly in both directions. Unlisted attributes stay
  invisible to the gate.
- **Source locations**: when a portable PDB is present (embedded or alongside the
  assembly), violations point at the offending file and line; without one they point at
  the contract file.

## Building

```
dotnet build
dotnet test
dotnet pack src/ContractGuard.MSBuild -c Release
```

Requires the .NET 8 SDK or later. Tests compile C# snippets in-memory with Roslyn, read
the emitted PE bytes back through the metadata reader, and extract the same model from the
Compilation through the symbol reader — asserting the two front-ends agree over the whole
edge-case zoo, which makes analyzer-vs-gate drift structurally impossible to ship.

ContractGuard eats its own cooking: `ContractGuard.Core`'s public surface is governed by
[its own contract](src/ContractGuard.Core/ContractGuard.Core.contract.json) through the
published `ContractGuard.MSBuild` package, so a PR that drifts the API fails its own gate.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
