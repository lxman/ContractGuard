# ContractGuard Owner's Manual

This is the practical guide. It assumes you want a working gate, not a tour of the design.
If you only read one section, read "Getting a gate running" and "Trim the contract."

## The idea

You write down the signatures your team must not change — in a JSON file that lives in the
repo next to the project. Developers (or AI coding agents) write whatever they want inside
the methods. When a build produces an assembly where a prescribed signature has drifted,
the build fails and tells you exactly what moved. The contract file is data, so changing
the rules means changing a reviewed file, not flipping a CI flag.

## Getting a gate running

You don't write the first contract by hand. Build your project, then point `extract` at
the result:

```
contractguard extract --assembly bin/Debug/net8.0/Shop.Domain.dll --output Shop.Domain.contract.json
```

That emits a contract covering every public and protected type in the assembly. Put it
next to the csproj, named `<AssemblyName>.contract.json`, and add the build gate:

```xml
<PackageReference Include="ContractGuard.MSBuild" Version="0.0.4-alpha" PrivateAssets="all" />
```

Build again. You'll see `ContractGuard: PASS` in the output. Change any prescribed
signature and the build fails with an error pointing at the contract file.

The CLI installs as a dotnet tool: `dotnet tool install -g ContractGuard --prerelease`
(the `--prerelease` flag matters while releases are alphas). Working from this repo
instead, `dotnet run --project src/ContractGuard.Cli --` stands in for the
`contractguard` command.

## Trim the contract

This is the habit that makes the tool useful. `extract` gives you everything, but a
contract that governs everything is just a snapshot — every refactor becomes a contract
edit. Delete down to what you actually care about: the service boundaries, the interfaces
other teams call, the surface you handed to a contractor. A type you delete from the file
is completely ungoverned. A member you delete from a listed type is governed only by that
type's `newMembers` setting.

So the default posture reads like this: the signatures in the file are locked, anything
else may come and go. That's deliberate, and it's probably what you want.

## The switches

Everything semantic lives in the contract's `settings` block. Nothing here can be
overridden from the command line or MSBuild — that's the point. If a knob could be turned
from CI config, a developer who can't touch the contract could still weaken the gate.

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

`defaultValues` deserves a word: default values compile into the *caller's* code, so
changing one silently changes behavior for everyone who recompiles. That's why it
defaults to significant.

`nullableAnnotations` is the one strictness knob that's off by default. Turning it on
makes `string` vs `string?` part of the contract, which is exactly right for a shop that
builds everything with nullable enabled — but assemblies compiled before nullable
reference types (or with it off) carry no annotations at all, and against those the
setting reports drift that's really just missing metadata. Turn it on deliberately.

`newMembers` and `parameterNames` can also be set per type, and `parameterNames` per
member, when one type or member needs a different rule than the rest.

`scope` is narrower than it sounds. A member you *prescribe* is enforced no matter its
accessibility — write `"access": "internal"` on a member and the gate checks it even with
the default scope. What scope actually decides is what the `deny` sweeps count as surface:
with the default, an internal helper added to a frozen type isn't a violation; add
`"internal"` to scope and it is. It also controls what `extract` pulls out — pass
`--scope public,protected,internal` to explore internal surface when bootstrapping a
contract for cross-team internal hooks.

## Postures

The settings combine into a handful of recognizable stances.

Lock the boundary, allow growth (the default — nothing to configure):

```json
"settings": {}
```

Frozen surface — nothing added, nothing changed. For plugin SDKs, deliverable
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

## Authoring from C# instead of JSON

If you'd rather write C# than JSON, the authoring verbs decompose it for you. `add` takes
member declarations on the command line:

```
contractguard add --contract Shop.contract.json --type OrderService "public Task<Result> Submit(Order order)"
contractguard add --contract Shop.contract.json --type OrderService --forbidden --reason "construct via DI" "public OrderService()"
```

`import` takes whole scaffold files - write the interfaces you're prescribing as a .cs
file and import it (`--assembly <name>` creates the contract if it doesn't exist yet):

```
contractguard import --contract Shop.contract.json --assembly Shop IOrderContract.cs
```

Both print the decomposed result so you can see what landed. Duplicates are skipped, the
file's usings carry over, and asking for `async` gets you the lecture about it not being
a signature concept. One syntax caveat on `import`: a base list can't reveal whether
`class Foo : Bar` extends a class or implements an interface, so the conventional
I-prefix decides - check the result if your interfaces are named unconventionally.

`normalize` rewrites a contract to canonical form, and `normalize --check` exits 1 when
the file isn't canonical, which makes it a cheap CI lint. Note that `//` comments don't
survive normalization.

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

The other kinds: `constructor` (params required, even `[]` — the param list is its
identity), `property` (with an `accessors` block for shapes like `{ get; private set; }`),
`indexer`, `event`, `field` (const values go in `value`), and `operator` (name is the
symbol: `"+"`, `"=="`, `"implicit"`).

Type names resolve like C#: add a `usings` array at the top of the contract and write
`Task<Result>` instead of fully qualified names. `access` defaults to public everywhere.
Comments (`//`) and trailing commas are tolerated, and the `$schema` line gets you
completion and validation in any decent editor.

## The build gate

The MSBuild package runs after every build, locally and in CI. Three properties control
operation (not semantics):

| Property | Effect | Default |
|---|---|---|
| `ContractGuardEnabled` | Skip the gate entirely | `true` |
| `ContractGuardContract` | Where the contract file is | `<project>/<AssemblyName>.contract.json` |
| `ContractGuardRequireContract` | Fail if the contract file is missing | `false` |

Projects without a contract file are skipped, so you can put the PackageReference in
`Directory.Build.props` once and add contracts only to the projects that need governing.

In CI, build with `-p:ContractGuardRequireContract=true`. Without it, deleting the
contract file deletes the gate, silently. The local default stays lenient so adding the
package to a fresh solution doesn't break forty projects at once.

## CI without MSBuild

The CLI works as a standalone pipeline step:

```
contractguard verify --contract Shop.Domain.contract.json --assembly out/Shop.Domain.dll
```

Exit 0 is a pass, 1 means violations (the diagnostics are on stdout), 2 means the
contract or assembly couldn't be loaded. `--format json` if you're feeding a dashboard,
`--format msbuild` if the output needs to be machine-parsed as build errors.

## Reading a failure

A violation looks like this:

```
CG0204: Type is 'long' but the contract prescribes 'int'. [Shop.Calc.Add] @ Services/Calc.cs(12)
```

The `@ file(line)` part appears when a portable PDB is available and points at the
offending member; in the IDE error list that location is what you click. Without a PDB,
errors point at the contract file instead.

The fix is one of two things: revert the code, or change the contract — and changing the
contract is a normal PR touching a file your repo can protect with CODEOWNERS. That's the
whole governance model.

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

## Common questions

**I added a method and the build failed.** That type has `newMembers: deny`. Either the
addition needs to go through contract review, or the type shouldn't be frozen — both are
decisions for whoever owns the contract file.

**I renamed a parameter and the build failed.** `parameterNames` is significant by
default because callers may use named arguments. If your shop doesn't care, set it to
`ignored` in the contract — in the file, where the change gets reviewed.

**How do I prescribe an overload?** Each overload is its own member entry. The parameter
types are the identity.

**`"default": null` or `{"$special": "default"}`?** Interchangeable at verification time —
the compiled metadata is identical for `= null` and `= default`, so the gate can't tell
them apart and treats them as one. Write whichever reads better.

**Why can't I require `async`?** It doesn't exist in the binary signature. Prescribe the
return type; whether the implementation is `async` is the implementer's business.

**Does the gate see what IL weavers do?** Yes. It reads the built assembly's metadata, not
the source, so post-compile rewriting (Fody and friends) is visible to it.
