# LAD Acceptance Checklist

Use one row per output, state, alarm, timer, counter, or mode decision.

## Static review

- [ ] Every variable exists and has the correct data type.
- [ ] Every network has one clear responsibility and a useful title.
- [ ] Every physical output has one authoritative writer.
- [ ] Stop, emergency stop, and critical fault dominate Start/Run.
- [ ] Opposite outputs contain cross-interlocks.
- [ ] Set/Reset priority is explicit.
- [ ] Timer/counter/edge instances are unique.
- [ ] Timer and counter pins use compatible types.
- [ ] No hidden combinational cycle exists.
- [ ] Manual/Auto/Maintenance mode selection is deterministic.
- [ ] Alarm reset requires the intended cause-clear condition.
- [ ] Power-up and invalid-state behavior are defined.
- [ ] `validate_lad_network` passes.
- [ ] Block compile passes.
- [ ] PLC compile passes.

## Behavioral test record

| Test | Initial state | Input/action | Expected state | Expected outputs | Observed | Pass |
|---|---|---|---|---|---|---|
| Power-up | | | | | | |
| Start | | | | | | |
| Stop | | | | | | |
| Emergency stop | | | | | | |
| Fault during run | | | | | | |
| Reset while cause active | | | | | | |
| Reset after cause clears | | | | | | |
| Opposite commands | | | | | | |
| Mode change stopped | | | | | | |
| Mode change running | | | | | | |
| Limit reached | | | | | | |
| Contradictory sensors | | | | | | |
| Timer boundary | | | | | | |
| Power cycle | | | | | | |
