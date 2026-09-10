# Component attribute fixtures

Real files, copied verbatim from a Tekla Structures 2026.0 installation, so the parser is tested
against the format Tekla actually writes rather than the `name=value` earlier notes assumed.

| File | What it is |
|---|---|
| `standard.j14000104` | A numbered system component's saved attributes. Lines are `joint_attributes.<NAME> <value>`. |
| `standard.p_GenericFormworkBeam` | A plugin's saved attributes. Same shape, prefix `GenericFormworkBeam_attributes.`, and note the `p_` in the FILE extension that the plugin's API name does not have. |

Two things these fixtures pin down:

- The format is whitespace-separated with a `<owner>_attributes.` prefix, **not** `name=value`.
  The queryable name is the part after the dot.
- `-2147483648` is Tekla's "not set" sentinel. The values in these files are worthless as data,
  which is why discovery uses them only to guess a type and `GetAttribute` decides what is set.
