---
name: tia-lad-hmi-engineering
description: Use when planning, generating, reviewing, or modifying Siemens TIA Portal V19 LAD logic or WinCC Advanced Classic HMI screens through the MCP tools in this repository. Enforces subnet-based integrated PLC-HMI connection, deterministic LAD logic, variable/type consistency, screen bounds, text fitting, compile/rollback, and simulation acceptance tests.
---

# TIA LAD + HMI Engineering Skill

## Scope

Use this skill for:

- Siemens TIA Portal V19 Update 4.
- STEP 7 Professional V19.
- WinCC Advanced Classic HMI.
- S7-1200 or S7-1500 projects.
- PLC programs generated in LAD through this MCP server.
- HMI variables and screens generated through this MCP server.

Do not claim compatibility with another TIA version until that version has its own validated toolchain, XML samples, and regression suite.

## Non-negotiable rules

1. Never treat successful XML import or successful compilation as proof that the machine logic is correct.
2. Never download to a real PLC or change CPU RUN/STOP unless the user explicitly requests it after reviewing the generated logic and test report.
3. Never create or import a separate Classic HMI integrated-connection XML for a PLC and HMI in the same TIA project.
4. An integrated HMI-to-PLC connection is established by connecting the PLC PN interface and HMI Ethernet interface to the same PN/IE subnet, assigning compatible IP settings, then compiling the hardware/HMI. TIA assigns the connection name.
5. Never invent a PLC tag, HMI tag, block, device, connection name, or address. Read the project first or declare the missing item and create it explicitly.
6. Before modifying an existing block or screen, export or snapshot the original object. On validation or compilation failure, restore it.
7. Every result must distinguish `success`, `warning`, `notVerified`, and `failure`. Do not report partial work as fully successful.

## Required inputs

Before generating logic, obtain or derive:

- PLC name and CPU family.
- HMI name and panel/runtime type.
- TIA version and update.
- Actual HMI canvas width and height.
- IO and internal tag list, including data types and addresses or symbolic paths.
- Functional sequence.
- Operating modes.
- Start, stop, emergency stop, fault, reset, and interlock rules.
- Power-up behavior.
- Expected outputs for each important input combination.

When any safety or process rule is unknown, mark it as unresolved. Do not silently choose behavior.

# Part A — Integrated PLC/HMI connection

## Correct connection workflow

For a PLC and Classic HMI in the same project:

1. Find or create one Ethernet/PROFINET subnet, normally `PN/IE_1`.
2. Locate the PLC's actual PN network interface and connect it to the subnet.
3. Locate the HMI's actual Ethernet interface and connect it to the same subnet.
4. Assign unique IP addresses in the same subnet and the same subnet mask.
5. Compile hardware and HMI.
6. Let TIA generate the integrated HMI connection and its default name.
7. Use that generated connection for PLC-backed HMI tags. Do not create a second connection.

The setup operation succeeds when the subnet and both device interfaces are verified, IP settings are valid, and project compilation reports no connection error. The ability to export the integrated connection is not a success criterion.

## Connection name policy

- Do not require a connection name when configuring the subnet.
- Prefer reading the generated name from TIA after compilation.
- If the current Openness surface cannot enumerate the integrated connection, accept an explicit user-supplied generated name.
- For the validated Chinese V19 Update 4 environment, `HMI_连接_1` is a possible TIA-generated name, but it must not be treated as universal.
- Never create `HMI_Connection`, `PLC_Connection`, or another guessed duplicate merely because enumeration returned no connection.

# Part B — LAD engineering workflow

## Mandatory sequence

1. Read existing blocks, block interfaces, PLC tag tables, and DB structures.
2. Convert the user's description into a functional table before writing LAD.
3. Build an output-ownership table: each physical or command output has one authoritative writer.
4. Divide logic into small networks with one responsibility each.
5. Produce `networkJson` for one network.
6. Call `validate_lad_network`.
7. Review semantic checks in this skill.
8. Call `add_lad_network` with `compileAfter=true`.
9. Read the generated network back with `read_lad_network` when available.
10. Compile the block and then the PLC.
11. Run the acceptance vectors in PLCSIM or a user-approved simulation environment.
12. Report both compile status and behavioral-test status.

Do not add more than five networks in one batch. For unfamiliar instruction structures, use one network per call.

## LAD network design rules

### Network responsibility

Each network must have:

- A unique number assigned by TIA.
- A concise title that describes the output or state being calculated.
- One main responsibility.
- Inputs and interlocks visible from left to right.
- Outputs, calls, or state transitions at the right side.

Recommended order:

1. Initialization and power-up state.
2. Mode selection.
3. Safety and permissive conditions.
4. Command generation.
5. Sequence or state transitions.
6. Physical output assignment.
7. Alarm generation.
8. HMI status mapping.

### Output ownership

- A normal `Coil` should be written in exactly one network unless the design explicitly documents multiple-writer behavior.
- Do not mix a normal coil with Set/Reset writes to the same variable.
- Physical outputs should normally be assigned from internal command variables in a dedicated output network.
- HMI buttons must not directly energize a physical output. They request a command; PLC permissives and interlocks decide the output.

### Stop and fault priority

- Stop, emergency stop, and critical-fault conditions must dominate start and run commands.
- For self-holding logic, put all stop/fault permissives before the Start OR Hold branch.
- For SR/RS logic, explicitly choose priority. Use reset-dominant behavior for stop and safety shutdown unless the functional specification says otherwise.
- A reset command may clear an alarm latch only when the alarm cause is no longer active, unless the specification explicitly requires acknowledgement while active.

### Modes

- Manual, automatic, maintenance, and local/remote modes must be mutually defined.
- Do not scatter mode decisions across unrelated output networks.
- Calculate one canonical mode state first, then consume it elsewhere.
- Define what happens when no mode or multiple modes are requested.

### Interlocks

- Mutually exclusive outputs such as Up/Down, Forward/Reverse, Open/Close must contain cross-interlocks.
- Include both command interlock and feedback/limit interlock where the available IO supports it.
- Define the response to contradictory feedback, such as both upper and lower limits active.

### Timers, counters, and edge detection

- Every TON, TOF, TP, TONR, CTU, CTD, CTUD, R_TRIG, and F_TRIG instance must be unique unless intentional sharing is documented.
- `PT` must be a valid Time value. Timer `Q` is Bool and `ET` is Time.
- Counter preset/current values must use compatible integer types.
- Every edge detector must have its own memory/instance.
- Do not generate hidden reuse of a default instance name.

### Data types

- Contacts and coils require Bool-compatible variables.
- Compare, arithmetic, MOVE, CONVERT, NORM_X, and SCALE_X instructions must declare compatible source and destination data types.
- Constants must match the instruction type.
- Every referenced variable must exist in the block interface, DB, or PLC tag table.

### State and sequence logic

For multi-step equipment, prefer an explicit state variable over many interacting latches.

For every state define:

- Entry condition.
- Active outputs.
- Exit condition.
- Timeout/fault condition.
- Stop behavior.
- Reset behavior.
- Invalid-state recovery.

Do not allow two states to be active unless the design intentionally uses bit-coded parallel states.

## LAD forbidden patterns

Reject the generated logic when any of these occurs:

- The same physical output is written in multiple unrelated networks.
- Start can bypass stop, emergency stop, or fault permissives.
- Up and Down, Forward and Reverse, or Open and Close can be true simultaneously.
- Set and Reset coils have undefined priority.
- Timer/counter/edge instances are reused accidentally.
- An output depends on itself through an undocumented combinational cycle.
- A tag is undeclared or has a mismatched type.
- A network contains an open branch, disconnected element, missing mandatory pin, or a coil before unfinished logic.
- A compile result is successful but no behavioral acceptance table has been executed.
- The AI modifies an existing block without a backup and rollback path.

## Mandatory behavioral acceptance vectors

At minimum test:

1. Power-up with all inputs inactive.
2. Start with all permissives valid.
3. Stop while running.
4. Emergency stop while running.
5. Fault appearing while running.
6. Reset while fault cause remains active.
7. Reset after fault cause clears.
8. Simultaneous opposite commands.
9. Mode change while stopped.
10. Mode change while running.
11. Limit-switch arrival.
12. Sensor contradiction or missing feedback.
13. Timer boundary immediately before, at, and after `PT`.
14. Loss and restoration of a command signal.
15. Power cycle or warm restart behavior.

Each vector must state initial conditions, input action, expected internal state, expected outputs, and observed result.

# Part C — HMI screen engineering workflow

## Mandatory sequence

1. Read the target HMI and actual screen canvas.
2. Read existing screens, HMI tags, and PLC tags.
3. Confirm the integrated network connection has been created through subnet configuration.
4. Create or verify HMI tags before creating bound controls.
5. Build a layout plan with rectangles before generating XML.
6. Normalize every item's position, size, font, and text.
7. Create the screen.
8. Read the screen back and run screen diagnostics.
9. Compile HMI.
10. Report out-of-bounds, overlap, clipping, missing-tag, and missing-event errors separately.

## Canvas and safe area

Always use the actual HMI canvas. Never assume a resolution from the prompt.

For an 800 × 420 screen, the project baseline is:

- Safe left/right margin: 10 px.
- Safe top margin: 5–10 px.
- Safe bottom margin: at least 10 px.
- Main usable width: 780 px.
- Title band example: left 10, top 5, width 780, height 28.
- Title font: approximately 20 px.
- Section labels: approximately 10–12 px.
- Standard button text: approximately 14 px.
- Status label text: approximately 10–11 px.
- Status lamps: 20–26 px diameter.

Scale these values for other resolutions. Do not copy coordinates blindly.

Every item must satisfy:

- `left >= safeMarginLeft`
- `top >= safeMarginTop`
- `left + width <= canvasWidth - safeMarginRight`
- `top + height <= canvasHeight - safeMarginBottom`

An item outside the canvas is a failure, not a warning.

## Grid and alignment

- Use an 8 px or 10 px design grid consistently.
- Align related labels, indicators, and buttons to common rows and columns.
- Use equal widths and heights for controls with the same role.
- Keep 8–16 px spacing between unrelated controls.
- Keep at least 4 px separation between normal objects unless overlap is intentional and documented.
- Background panels may contain controls; ordinary controls must not significantly overlap each other.

## Text fitting

Never create a text field or button whose text is clipped.

Use this fallback order:

1. Increase the control width or height while staying inside the canvas.
2. Reduce the font within the allowed range.
3. Wrap text only when the control type and design support wrapping.
4. Shorten the label only when the user approves the wording.
5. Otherwise reject the layout and report the item.

For approximate fitting when exact font metrics are unavailable:

- Chinese/full-width character weight: 1.0.
- Uppercase letter or digit weight: 0.68.
- Other Latin character weight: 0.56.
- Space weight: 0.35.
- Estimated required width: `weightedLength × fontSize × 1.25 to 1.35 + horizontal padding`.
- Estimated required height: `fontSize × 1.5 to 1.65 + vertical padding`.

Recommended project font ranges for 800 × 420:

- Title: 18–22.
- Section heading: 10–12.
- Button: 12–16.
- Status/field label: 10–12.
- Numeric IO field: 14–24 depending on importance.
- Footer/help text: 9–10.

Do not reduce normal operational text below 9 px on this canvas.

## Touch controls

For touch-operated controls:

- Prefer a minimum hit area around 44 × 36 px or larger.
- Critical buttons require clear spacing from adjacent controls.
- Momentary commands must define Press and Release behavior.
- Maintained selections must show the active state.
- A button must have visible feedback, enabled/disabled logic, and a bound HMI tag or event.
- Emergency-stop functions must not be implemented as a normal HMI-only safety function.

## Color semantics

Use color consistently:

- Grey: inactive or unavailable.
- Green: running, healthy, or confirmed active.
- Yellow/amber: warning, transition, or attention.
- Red: alarm, trip, or unsafe state.
- Blue/cyan: informational state when needed.

Do not rely on color alone. Pair critical states with text, symbol, or shape.

## Object naming

Use stable prefixes:

- `txt_` or `lb_`: static text.
- `btn_` or `bn_`: button.
- `lamp_` or `ci_`: indicator.
- `io_`: input/output field.
- `sw_`: switch.
- `panel_`: background/group panel.
- `nav_`: navigation control.

Names must be unique within the screen and must not be generated from coordinates alone.

## HMI tag rules

- Create PLC tags or DB variables first.
- For integrated same-project communication, prefer symbolic binding.
- Use absolute HMI tags only when explicitly required and verify address width against the data type.
- Screen controls bind to HMI tags; the HMI tag then binds to the PLC symbol/address.
- An internal HMI tag such as a screen-number variable does not prove that a PLC connection exists.
- Every tag referenced by a screen must exist before screen import.
- Every screen tag reference must match spelling and case exactly.

## HMI forbidden patterns

Reject the generated screen when any of these occurs:

- Any control is outside the actual canvas.
- Text is clipped or unreadably small.
- Two actionable controls significantly overlap.
- A critical button has no feedback or release behavior.
- A screen references a missing HMI tag.
- An HMI tag has a missing or guessed connection.
- Opposite commands are placed so closely that accidental activation is likely.
- Alarm colors are inconsistent across screens.
- The screen is created before the tag model is complete.
- The AI treats a generated default connection name as universal across languages or TIA versions.

# Part D — Tool-use policy for this repository

## PLC/LAD tools

Preferred sequence:

1. `list_blocks`
2. `list_tag_tables` and `read_tag_table`
3. `get_block_interface` or the enhanced interface reader available in the running build
4. `validate_lad_network`
5. `add_lad_network` with `compileAfter=true`
6. `read_lad_network`
7. `compile_block`
8. `compile_plc`

Use `add_lad_networks_batch` only for up to five already-validated networks.

## Network/HMI tools

Preferred sequence:

1. `list_devices`
2. `setup_network_and_hmi_connection` in integrated subnet mode
3. `compile_all_hardware`
4. Read or confirm the TIA-generated default connection name when needed
5. Create/import HMI tags
6. `create_hmi_screen_from_spec`
7. `list_hmi_screen_items` or `diagnose_screen`
8. HMI compile tool or `compile_and_verify`

Do not call `create_hmi_connection` for the normal same-project integrated workflow.

## Dangerous tools

Do not call without explicit user approval:

- Download tools.
- Upload-from-device tools.
- CPU RUN/STOP tools.
- Delete project/device/block tools.
- Global automatic confirmation handlers.

# Part E — Required final report

Return a structured summary containing:

- Environment and TIA version.
- Objects created or modified.
- Backup/rollback status.
- Network and connection verification.
- LAD validation results per network.
- PLC compile result.
- Behavioral tests executed and pass/fail counts.
- HMI canvas size.
- HMI out-of-bounds count.
- HMI overlap count.
- HMI text-clipping count.
- Missing or unresolved tag bindings.
- HMI compile result.
- Explicit unresolved assumptions.
- Production-readiness decision: `not_ready`, `simulation_ready`, `controlled_pilot_ready`, or `production_ready`.

Never output `production_ready` solely because compilation succeeded.
