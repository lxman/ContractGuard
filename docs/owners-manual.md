# ContractGuard Owner's Manual

If you've never used ContractGuard, start at the top and follow "Your first gate" all the
way through. By the end you'll have a real gate that fails the build when an API signature
drifts. Everything after that is detail on the steps you just took, plus reference material
for when you need it.

## What it does

You write down the signatures your team isn't allowed to change, in a JSON file that lives
in the repo next to the project. Anyone (a developer, an AI coding agent) writes whatever
they want inside the methods. When a build produces an assembly where one of those
signatures has moved, the build fails and tells you exactly what moved.

The contract is data, not code. Changing the rules means editing a reviewed file, not
flipping a switch in CI, so you put the file under CODEOWNERS and "an architect has to
approve API changes" comes for free. And because the gate reads the built assembly's
metadata, it checks the thing that actually ships rather than the source.

## Your first gate, end to end

Say you have a library `Shop.Domain` and you want its service boundary frozen. Here's the
whole loop, start to finish.

**1. Install the CLI.**

```
dotnet tool install -g ContractGuard --prerelease
```

The `--prerelease` flag matters while the releases are alphas. If you're working inside
this repo instead of installing the tool, `dotnet run --project src/ContractGuard.Cli --`
stands in for the `contractguard` command everywhere below.

**2. Build, then extract a starting contract.**

You don't write the first contract by hand. Build the project, then point `extract` at the
DLL it produced:

```
contractguard extract --assembly bin/Debug/net8.0/Shop.Domain.dll --output Shop.Domain.contract.json
```

That writes a contract covering every public and protected type in the assembly. Open the
file and look: it's a list of types, each with its members broken into elements (return
type, parameters, modifiers). You don't need to understand every field yet.

**3. Trim it down.**

`extract` gives you everything, and everything is too much. A contract that governs every
type turns every small refactor into a contract edit. Delete down to what you actually
care about: the service boundary, the interfaces other teams call, the surface you handed
to a contractor. Whatever you leave in the file is locked. Whatever you delete is free to
change. There's a whole section on this below, because it's the habit that makes the tool
worth having.

**4. Add the gate to the project.**

```xml
<PackageReference Include="ContractGuard.MSBuild" Version="0.0.10-alpha" PrivateAssets="all" />
```

The contract file has to sit next to the csproj, named `<AssemblyName>.contract.json`. For
an assembly named `Shop.Domain` that's `Shop.Domain.contract.json`. That's the default
location and the gate finds it on its own.

**5. Build and watch it pass.**

```
dotnet build
```

You'll see `ContractGuard: PASS (12 governed types)` in the build output. The gate ran,
read the compiled assembly, and everything in the contract matched.

**6. Break something on purpose.**

This is the part that builds trust. Change a prescribed method: flip a return type from
`int` to `long`, rename a parameter, drop a `virtual`. Build again:

```
CG0204: Type is 'long' but the contract prescribes 'int'. [Shop.Calc.Add] @ Services/Calc.cs(12)
```

The build fails with a CG code, the member that drifted, and (when a PDB is around) the
file and line. Revert the change and it goes back to passing.

**7. Decide: revert, or change the contract.**

There are only ever two ways out of a failure. Put the code back, or change the contract to
allow the new signature. Changing the contract is a normal PR touching a file your repo can
protect with CODEOWNERS. Code changes are free, contract changes are reviewed. That's the
entire governance model.

## Trim the contract

This is the habit worth forming. `extract` hands you a snapshot of everything; a contract
that governs everything means every refactor becomes a contract edit. Cut it down to the
surface you actually want to hold still.

A type you delete from the file is completely ungoverned. A member you delete from a type
that stays is governed only by that type's `newMembers` setting (more on that below). So
the default posture reads like this: the signatures left in the file are locked, anything
else may come and go. That's usually what you want.

## Testing the gate

Before you rely on it, prove it bites. Pick a governed signature and break it on purpose,
the way step 6 did. Build. You should get the matching CG code; revert and it passes again.
If nothing happens, the gate isn't wired to that project. Check that the contract is named
`<AssemblyName>.contract.json` and sits next to the csproj.

There's a wrinkle for a class whose every method is pinned to an interface. You can't test
it by changing a return type, because that breaks the build for an unrelated reason (the
class stops implementing the interface). Test those with a change the interface doesn't
constrain instead: rename a parameter, which trips `CG0205`, or add `virtual` to a method,
which trips `CG0203`. Either one proves the gate is reading that type.

The thing that surprises people: private members are invisible. The gate governs the
surface your `scope` covers, which is public and protected by default. A changed private
helper won't ever trip it, by design. If you do want a private member governed, prescribe
it explicitly. A member that's written into the contract is enforced no matter its
accessibility.

## Live errors while you type

The gate runs at build time. If you also want violations to show up in the editor as you
write them, add the analyzer alongside the gate:

```xml
<PackageReference Include="ContractGuard.MSBuild" Version="0.0.10-alpha" PrivateAssets="all" />
<PackageReference Include="ContractGuard.Analyzers" Version="0.0.10-alpha" PrivateAssets="all" />
```

Two packages, two jobs. The MSBuild package is the gate (it runs on every build and in CI)
and it's also what hands the contract file to the compiler. The Analyzers package is the
editor experience: the same CG codes, on the offending line, live. You want both. The
analyzer alone has no contract handed to it; the gate alone gives you build errors but no
squiggles.

One thing to keep straight: the analyzer is a convenience, never the gate. Anyone can turn
analyzers off locally, which is fine, because the build-time check (and CI) still runs the
real verification against the compiled assembly.

## Reading a failure

A violation looks like this:

```
CG0204: Type is 'long' but the contract prescribes 'int'. [Shop.Calc.Add] @ Services/Calc.cs(12)
```

The `@ file(line)` part shows up when a portable PDB is available, and it points at the
member that moved. In the IDE error list that location is what you click. Without a PDB the
error points at the contract file instead.

Every code has a one-line meaning in the table at the end of this guide, and a full entry
in [docs/diagnostics.md](diagnostics.md) — that's also where the error-code links in your
IDE land. The short version of any failure: the code drifted from the contract. Revert it,
or change the contract through review.

---

The rest of this guide is reference. You can stop here and come back when you hit something.

## The settings

Everything semantic lives in the contract's `settings` block. Nothing here can be
overridden from the command line or MSBuild, and that's the point: if a knob could be
turned from CI config, a developer who can't touch the contract could still weaken the gate.

| Setting | The question it answers | Default |
|---|---|---|
| `newTypes` | May developers add new public types at all? | `allow` |
| `newMembers` | May governed types gain members beyond those listed? | `allow` |
| `parameterNames` | Is renaming a parameter a violation? | `significant` |
| `defaultValues` | Is changing `= 0` to `= 1` a violation? | `significant` |
| `nullableAnnotations` | Is `string?` vs `string` a violation? | `ignored` |
| `tupleElementNames` | Is `(int x, int y)` vs `(int a, int b)` a violation? | `significant` |
| `scope` | Which accessibility levels the deny sweeps treat as surface | `["public", "protected"]` |
| `significantAttributes` | Which attributes the gate pays attention to | none |

Most of these you'll never touch. The defaults give you "locked signatures, open to
additions," which is the common case. Three are worth knowing about.

`defaultValues` is significant because a default value compiles into the *caller's* code.
Change `= 0` to `= 1` and everyone who recompiles silently gets the new behavior, so the
gate treats it as a real change.

`nullableAnnotations` is the one strictness knob that's off by default. Turn it on and
`string` vs `string?` becomes part of the contract, which is right for a shop that builds
everything with nullable enabled. But an assembly compiled before nullable reference types
(or with it off) carries no annotations at all, and against that the setting reports drift
that's really just missing metadata. It also governs the nullable-flavored generic
constraints, `notnull` and `class?`. Turn it on deliberately.

`scope` is narrower than it sounds. A member you *prescribe* is enforced no matter its
accessibility. Write `"access": "internal"` on a member and the gate checks it even with
the default scope. What scope actually decides is what the deny sweeps count as surface:
with the default, an internal helper added to a frozen type isn't a violation; add
`"internal"` to scope and it is. It also controls what `extract` pulls out, so
`--scope public,protected,internal` explores internal surface when you're bootstrapping a
contract for cross-team internal hooks.

`newMembers` and `parameterNames` can also be set per type, and `parameterNames` per
member, when one spot needs a different rule than the rest.

## Postures

The settings combine into a handful of recognizable stances.

Lock the boundary, allow growth (the default, nothing to configure):

```json
"settings": {}
```

Frozen surface, nothing added and nothing changed. For plugin SDKs, deliverable
verification, grading student submissions:

```json
"settings": { "newTypes": "deny", "newMembers": "deny" }
```

One strict type in an otherwise open assembly:

```json
{ "type": "Shop.OrderService", "newMembers": "deny", "members": [ ... ] }
```

Forbidding a specific member is its own mode, and `reason` shows up verbatim in the build
error:

```json
{ "kind": "constructor", "params": [], "mode": "forbidden",
  "reason": "Construct through DI; this bypasses validation." }
```

## The build gate

The MSBuild package runs after every build, locally and in CI. Three properties control how
it operates (not what it enforces, which is the contract's job):

| Property | Effect | Default |
|---|---|---|
| `ContractGuardEnabled` | Skip the gate entirely | `true` |
| `ContractGuardContract` | Where the contract file is | `<project>/<AssemblyName>.contract.json` |
| `ContractGuardRequireContract` | Fail if the contract file is missing | `false` |

Projects without a contract file are skipped, so you can put the PackageReference in
`Directory.Build.props` once and add contracts only to the projects that need governing.

In CI, build with `-p:ContractGuardRequireContract=true`. Without it, deleting the contract
file deletes the gate, silently. The local default stays lenient so adding the package to a
fresh solution doesn't break forty projects at once.

## Authoring from C# instead of JSON

If you'd rather write C# than JSON, the authoring verbs decompose it for you. `add` takes
member declarations on the command line:

```
contractguard add --contract Shop.contract.json --type OrderService "public Task<Result> Submit(Order order)"
contractguard add --contract Shop.contract.json --type OrderService --forbidden --reason "construct via DI" "public OrderService()"
```

`import` takes whole scaffold files. Write the interfaces you're prescribing as a .cs file
and import it (`--assembly <name>` creates the contract if it doesn't exist yet):

```
contractguard import --contract Shop.contract.json --assembly Shop IOrderContract.cs
```

Both print the decomposed result so you can see what landed. Duplicates are skipped, the
file's usings carry over, and asking for `async` gets you the lecture about it not being a
signature concept. One caveat on `import`: a base list can't reveal whether `class Foo : Bar`
extends a class or implements an interface, so the conventional I-prefix decides. Check the
result if your interfaces are named unconventionally.

`normalize` rewrites a contract to canonical form, and `normalize --check` exits 1 when the
file isn't canonical, which makes it a cheap CI lint. Note that `//` comments don't survive
normalization.

## Writing members by hand

You'll mostly edit what `extract` produced, but here's the vocabulary. A parameter is
either the `["type", "name"]` pair or an object when it needs a modifier or default:

```json
{ "kind": "method", "name": "Find", "returns": "T",
  "typeParams": [{ "name": "T", "constraints": ["struct"] }],
  "params": [
    { "type": "Span<T>", "name": "buffer", "modifier": "ref" },
    { "type": "int", "name": "start", "default": 0 }
  ] }
```

The other kinds: `constructor` (params required, even `[]`, because the param list is its
identity), `property` (with an `accessors` block for shapes like `{ get; private set; }`),
`indexer`, `event`, `field` (const values go in `value`), and `operator` (name is the
symbol: `"+"`, `"=="`, `"implicit"`).

Type names resolve like C#: add a `usings` array at the top of the contract and write
`Task<Result>` instead of the fully qualified name. `access` defaults to public everywhere.
Comments (`//`) and trailing commas are tolerated, and the `$schema` line gets you
completion and validation in any decent editor.

A constant value is a plain JSON string, number, boolean, or null. Two cases have no JSON
literal and use an object form instead: `default(T)` for a value type is
`{"$special": "default"}`, and a non-finite floating-point constant is
`{"$special": "NaN"}`, `{"$special": "Infinity"}`, or `{"$special": "-Infinity"}`. `extract`
emits all of these for you; you'll rarely type one.

## CI without MSBuild

The CLI works as a standalone pipeline step:

```
contractguard verify --contract Shop.Domain.contract.json --assembly out/Shop.Domain.dll
```

Exit 0 is a pass, 1 means violations (the diagnostics are on stdout), 2 means the contract
or assembly couldn't be loaded. Use `--format json` if you're feeding a dashboard, or
`--format msbuild` if the output needs to be parsed as build errors.

## Common questions

**I added a method and the build failed.** That type has `newMembers: deny`. Either the
addition goes through contract review, or the type shouldn't be frozen. Both are decisions
for whoever owns the contract file.

**I renamed a parameter and the build failed.** `parameterNames` is significant by default,
because callers may use named arguments. If your shop doesn't care, set it to `ignored` in
the contract, where the change gets reviewed.

**How do I prescribe an overload?** Each overload is its own member entry. The parameter
types are the identity.

**`"default": null` or `{"$special": "default"}`?** Interchangeable at verification time.
The compiled metadata is identical for `= null` and `= default`, so the gate can't tell
them apart and treats them as one. Write whichever reads better.

**Why can't I require `async`?** It doesn't exist in the binary signature. Prescribe the
return type; whether the implementation is `async` is the implementer's business.

**Does the gate see what IL weavers do?** Yes. It reads the built assembly's metadata, not
the source, so post-compile rewriting (Fody and friends) is visible to it.

**I pointed it at a DLL and got "no .NET metadata."** The file is a native (unmanaged)
binary, not a managed assembly. `extract` and `verify` only read .NET metadata; they report
this as a clean error rather than guessing.

## The diagnostic codes

The full reference, one entry per code, is in [docs/diagnostics.md](diagnostics.md). The
short table:

| ID | Meaning |
|---|---|
| CG0001 | Contract names a different assembly than the one scanned |
| CG0100 | Governed type is missing |
| CG0101–CG0107 | Type-level drift: kind, modifiers, base type, interface, generic params, enum underlying type, delegate signature |
| CG0200 | Prescribed member is missing entirely |
| CG0201 | Member found but signature changed (the closest match is shown) |
| CG0202–CG0211 | Member-level drift: accessibility, modifiers, type, parameter names, defaults, accessors, const value, generic params, parameter modifiers, significant attributes |
| CG0300 | A forbidden member exists |
| CG0400 | Member not in the contract, and `newMembers` is `deny` |
| CG0500 | Type not in the contract, and `newTypes` is `deny` |
