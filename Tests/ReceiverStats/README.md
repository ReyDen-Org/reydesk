# Receiver statistics regression

Run from PowerShell, from the repository root:

```powershell
& './Tests/ReceiverStats/run.ps1'
```

Uses a local JDK (`JAVA_HOME`) and the Kotlin compiler bundled in a Gradle install (`GRADLE_HOME`), without downloading
or installing packages. Or pass `-JavaHome` / `-GradleHome`. Compiles
the actual production counter helper with the tests; generated JAR stays in TEMP.

The twenty cases catch accidentally reporting lifetime delay as interval delay,
missing fields/baselines, stalled counters, counter resets, nonfinite counters and
valid zero processing time. Rate/loss cases verify elapsed intervals, missing
baselines, resets, negative packet corrections, actual zero loss and idle traffic.
Throughput is received RTP video payload, excluding transport overhead; FPS is
decoded frames, rather than the configured ceiling. RTT is the selected ICE
candidate pair's network RTT, not screen-to-phone latency. Phone preferences also
verify invalid-value normalization and host-default inheritance.
This does not measure screen-to-phone latency or test
WebRTC's native implementation. The application build validates its API calls.
