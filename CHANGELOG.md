# Changelog

## Unreleased

- Fix local gateway setup rejecting its own newly started service as a port conflict. Verify the service PID and include available process names for genuine conflicts. Related context: #547 from @ranjeshj.
