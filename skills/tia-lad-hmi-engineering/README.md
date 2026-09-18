# TIA LAD/HMI Skill Package

Place this folder where the agent or host application loads skills, or inject `SKILL.md` as the engineering system instruction before exposing TIA MCP tools.

Recommended loading order:

1. Load `SKILL.md`.
2. Load the current project summary and variable lists.
3. Expose only the whitelisted engineering tools.
4. Keep download, upload, CPU state, and delete tools disabled by default.
5. Require the final readiness report defined by the skill.

This skill does not replace code-level validators. It constrains planning and tool use; `QualityGuardService`, compile checks, rollback, and PLCSIM acceptance remain mandatory.
