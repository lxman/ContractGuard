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

Early scaffold. Core engine, CLI, and MSBuild gate work end to end. See
[What the gate can and can't see](#what-the-gate-can-and-cant-see) for the current
enforcement boundary; the master list of gaps lives as TODOs in code.

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
contractguard extract --assembly Shop.Domain.dll --output Shop.Domain.contract.json
contractguard verify  --contract Shop.Domain.contract.json --assembly Shop.Domain.dll
contractguard show    --contract Shop.Domain.contract.json
```

`extract` takes `--scope public,protected,internal,private` to pull out more than the
default public+protected surface — prescribed members of any accessibility are enforced
either way; scope governs what the deny sweeps and extraction consider surface.

`extract` bootstraps a contract from a golden build; `verify` is the gate (exit 0 pass,
1 violations, 2 errors); `show` renders the elements back as C# declarations.

**MSBuild package** (the drop-in):

```xml
<PackageReference Include="ContractGuard.MSBuild" Version="0.0.1-alpha" PrivateAssets="all" />
```

After every build, `<project>/<AssemblyName>.contract.json` is verified automatically —
violations land in the IDE error list pointing at the contract file. Projects without a
contract file are skipped, so the reference can live solution-wide in Directory.Build.props.
In CI, build with `-p:ContractGuardRequireContract=true` so a deleted contract file fails
the build instead of silently removing the gate.

## What the gate can and can't see

The gate reads assembly metadata, so its enforcement boundary is metadata's boundary. Two
kinds of limits apply — ones that are permanent, and ones that are just not built yet.

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

### Not decoded yet — accepted by the schema, but not enforced

- **Constraint and inheritance nullability.** `where T : class?` / `notnull` constraints
  and annotations on base types and implemented interfaces are not decoded.
- **Enum parameter defaults.** Write them as the underlying numeric value
  (`"default": 0`); `"default": "OrderStatus.Pending"` strings are not resolved yet and
  will report a mismatch.
- **Records.** Classify as class/struct in metadata; `"kind": "record"` is accepted and
  matched as `class` (`record-struct` as `struct`). Compiler-synthesized record members are
  visible to the gate like any other member.
- **`ref readonly` returns and parameters, `volatile` fields.** The modreqs are not decoded;
  `ref readonly` currently reads as plain `ref`.
- **Explicit interface implementations** are skipped and cannot be governed yet.
- **`significantAttributes`** is accepted by the schema but attribute comparison is not
  implemented.

## Building

```
dotnet build
dotnet test
dotnet pack src/ContractGuard.MSBuild -c Release
```

Requires the .NET 8 SDK or later. Tests compile C# snippets in-memory with Roslyn and read
the emitted PE bytes back through the metadata reader — the same harness that will pin
metadata-vs-ISymbol front-end consistency when the Roslyn analyzer (phase 2) lands.

ContractGuard eats its own cooking: `ContractGuard.Core`'s public surface is governed by
[its own contract](src/ContractGuard.Core/ContractGuard.Core.contract.json) through the
published `ContractGuard.MSBuild` package, so a PR that drifts the API fails its own gate.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
