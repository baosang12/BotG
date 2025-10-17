# Audit Tools Notes
- Avoid false-positives for `run:` lines:
  * Ignore `defaults: run:` blocks
  * Treat `run: |` as NON-empty unless the next indented line is blank or dedent
