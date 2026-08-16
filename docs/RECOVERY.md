# How the source was recovered

The C# for this mod was lost; only the shipped `SoundBlocks.dll` (13,312 bytes,
built 2018) survived. `SoundBlocksScripts/` was reconstructed from that assembly
and then checked against it. This is the record of how, and of how much the
result can be trusted.

## The tooling

No .NET toolchain is installed on this machine and none was added. Two things
did the work:

- **Reading the assembly**: `dnfile` + `dncil` (pure Python) in a throwaway
  virtualenv. `dnfile` parses the CLI metadata tables; `dncil` decodes method
  bodies into instructions. Neither decodes *signatures* — field types, method
  parameters, generic instantiations, local variable types, custom attribute
  arguments — so those were decoded by hand against ECMA-335 II.23.2.
- **Rebuilding it**: Besiege's own `mcs.dll`, driven offline through the game's
  `libmono.so`. That is what `tools/build.sh` does, and it is the reason the
  comparison below is meaningful: the same compiler that produced the original
  produced the replacement.

Three gotchas cost time and are worth writing down:

- `dncil` hands back a raw `StringToken` for `ldstr` rather than the text. Every
  string in the first dump read as a nine-digit number (`"1879048193"` is
  `0x70000001`). The text lives in the `#US` heap, reachable as
  `pe.net.user_strings.get(token.rid)`.
- A branch operand from `dncil` is an **absolute stream offset**, not the
  relative displacement in the encoding. Treating it as relative produced
  targets like `IL_0875` in a method 0x460 bytes long.
- A method body reader must subclass `CilMethodBodyReaderBase`. Supplying a
  duck-typed object with `read`/`tell`/`seek` fails on `read_uint8`, which is
  defined on the base class.

## What the assembly gave up

Everything structural survives compilation and was read directly rather than
guessed: the four types and their base types, every field with its type and its
`public`/`private`/`static` flags, every method signature and its accessibility,
the `[XmlRoot]` / `[XmlArray]` / `[XmlArrayItem]` / `[XmlAttribute]` arguments
that name the XML elements, the `Text` auto-property (a `<Text>k__BackingField`
plus a `MethodSemantics` row pairing the accessors), and all four assembly
references.

What does **not** survive is what you would expect: local variable names,
parameter names of private methods where the `Param` table was not emitted,
comments, and the file layout. Local names in the reconstruction are chosen for
readability — `velocityPitch` in `SimulateUpdateAlways` was `V_11`.

## How the reconstruction was checked

Not by reading it. `tools/build.sh` compiled the reconstructed sources, and both
assemblies were dumped to a normalised instruction stream — branch operands
resolved to the *ordinal* of the target instruction, so the two are comparable
even though they have different byte offsets — and compared method by method.

Result: **23 methods, 12 byte-identical, 11 differing only in ways that are
purely a Debug-vs-Release codegen difference.** The original is a Debug build
(it carries `[Debuggable]`, and every method is full of `nop`), which shows up
as exactly four patterns:

| in the original (Debug) | in the rebuild (Release) |
| --- | --- |
| `nop` between every statement | absent |
| every condition spilled: `stloc.N` / `ldloc.N` / `brfalse` | `brfalse` straight off the stack |
| `clt.un` / `ldc.i4.0` / `ceq` / `brfalse` | the fused `blt.un` |
| every `return` routed through one shared exit `ret` | `ret` in place |

Nothing else differs. No instruction is present in one and missing in the other,
no call goes to a different member, no constant differs. The reconstruction is
exact.

That comparison is worth keeping if the sources are ever touched again in a way
that is meant to be behaviour-preserving; the scripts that produced it are not
in the repo, but the method is a page of Python.

## Reading the comparison the other way

Two things the check does **not** prove, and neither is a defect in the method:

- It says the reconstruction matches the 2018 assembly. It says nothing about
  whether the 2018 assembly was *correct* — and it was not. See the fixes in
  [AGENTS.md](../AGENTS.md#what-was-wrong-with-it).
- `float` constants in the dump are printed as their decimal expansions
  (`0.0020000000949949026`). Those are exactly `0.002f` and friends; the source
  uses the short form and recompiles to the same bits.
