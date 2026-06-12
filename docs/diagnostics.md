# ContractGuard diagnostics

Every violation carries a CG code, identical across the CI gate, the MSBuild check, and
the editor analyzer. The fix for any of them is one of two things: change the code back,
or change the contract — and the contract is a reviewed file, so changing it is a normal
PR through whatever protection (CODEOWNERS) your repo puts on it.

## CG0001

**Contract governs a different assembly.** The contract's `assembly` field doesn't match
the scanned assembly's name. Usually a copied contract or a renamed `AssemblyName`. The
analyzer ignores non-matching contracts silently; the gate reports this instead, so a
wired-up contract that stops matching never passes by accident.

## CG0002

**Contract file is invalid.** The contract failed to parse — malformed JSON, an unknown
property (the format is strict so typos can't silently weaken governance), or a value
outside the schema. Reported instead of silently leaving the assembly ungoverned. The
message carries the parse error; your editor shows the same problem inline if the file
has the `$schema` reference.

## CG0100

**Governed type is missing.** A type the contract prescribes doesn't exist in the
assembly. Renamed, deleted, or moved namespace — to the gate these are all the same
thing: the prescribed name is gone.

## CG0101

**Type kind does not match.** Class became struct, struct became record struct, class
became interface. Kind is part of a governed type's identity when the contract states it.

## CG0102

**Type modifiers do not match.** `static`, `abstract`, `sealed`, `readonly` (structs), or
`ref` (structs) changed relative to the prescription.

## CG0103

**Base type does not match.** The prescribed base class changed or was removed.

## CG0104

**Prescribed interface is not implemented.** An interface the contract requires is no
longer implemented. The contract records the transitive closure of declared interfaces,
so removing a base interface from a declared one also triggers this.

## CG0105

**Type generic parameters do not match.** Count, variance (`in`/`out`), or constraints on
the type's generic parameters changed.

## CG0106

**Enum underlying type does not match.** `enum Color : byte` became `enum Color : int`,
or similar — a binary-breaking change for consumers.

## CG0107

**Delegate signature does not match.** The delegate's return type or parameter list
drifted from the prescription.

## CG0200

**Prescribed member is missing.** No member of the right kind and name exists. Deleted or
renamed; if a same-name overload still exists you get CG0201 instead.

## CG0201

**Member signature changed.** A member with the prescribed name exists but its parameter
types or generic arity differ. The message shows both: prescribed and found.

## CG0202

**Accessibility does not match.** `public` became `internal`, a setter went `private` —
whatever the prescription says, accessibility is part of it. (Operators and explicit
interface implementations carry no accessibility and are exempt.)

## CG0203

**Modifiers do not match.** `static`, `abstract`, `virtual`, `sealed`, `override`,
`readonly`, `const`, or `volatile` drifted. Note that `async` is deliberately not a
contract concept — it's invisible in the binary signature.

## CG0204

**Type does not match.** The return type of a method, or the declared type of a
property, field, or event, differs from the prescription. Also raised when a `ref`
return kind changes.

## CG0205

**Parameter names do not match.** A parameter was renamed and `parameterNames` is
significant (the default — callers may use named arguments). Set it to `ignored` in the
contract if your shop doesn't care, per type or per member if one spot needs an
exception.

## CG0206

**Parameter defaults do not match.** A default value changed, was added, or was removed.
Defaults compile into call sites, so changing one silently changes recompiled callers'
behavior — which is why `defaultValues` is significant by default.

## CG0207

**Accessors do not match.** The property's accessor shape drifted: a setter appeared or
disappeared, `set` became `init`, or an accessor's accessibility changed relative to the
prescribed `accessors` block.

## CG0208

**Constant value changed.** A `const` field's value differs from the prescription.
Consumers compile const values in, so this is a silent behavior change for them.

## CG0209

**Member generic parameters do not match.** A method's generic parameter count or
constraints changed.

## CG0210

**Parameter modifiers do not match.** `ref`, `out`, `in`, `ref readonly`, `params`, or
extension-method `this` changed on a parameter.

## CG0211

**Significant attributes do not match.** Within the attribute universe listed in
`settings.significantAttributes`, presence must match the prescription exactly — both a
missing prescribed attribute and an unprescribed present one report here. Attribute
arguments are not compared, only presence.

## CG0300

**Forbidden member is present.** The contract prescribes this exact signature with
`"mode": "forbidden"` and it exists anyway. The entry's `reason` is included in the
message — that's the architect telling you why.

## CG0400

**Member is not part of the contract.** The type has `newMembers: deny` (directly or via
settings) and a member exists that the contract doesn't prescribe. Additions to a frozen
type go through contract review first.

## CG0500

**Type is not part of the contract.** `settings.newTypes` is `deny` and an unprescribed
type exists within the governed scope.
