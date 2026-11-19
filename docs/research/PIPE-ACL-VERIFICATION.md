# Pipe ACL verification (network-logon deny)

**Verdict: INCONCLUSIVE for "network clients are refused". PASS for "a local current-user client can connect".**

Date: 2026-11-19. Windows 11 Home 10.0.26200, Windows PowerShell 5.1 (.NET Framework `PipeSecurity`), one machine, no second host.

## What was tested

Server pipes (`NamedPipeServerStream`, 1 instance, byte mode, asynchronous) in three configurations, each attacked
from the same user with `NamedPipeClientStream` using four host names (`.`, `localhost`, `127.0.0.1`, the computer name):

1. Default ACL (control).
2. Explicit ACL: Allow current user FullControl only (control, no deny).
3. Explicit ACL as GlyphLens uses it: Deny `NT AUTHORITY\NETWORK` (S-1-5-2) FullControl, then Allow current user FullControl.

Script: `scratchpad/acl_exp.ps1` (not in the repository).

## Results (verbatim)

```
control-default-ACL                  host=.          -> CONNECTED
control-default-ACL                  host=localhost  -> CONNECTED
control-default-ACL                  host=127.0.0.1  -> CONNECTED
control-default-ACL                  host=OJESH      -> CONNECTED
control-allow-user-only(no deny)     host=.          -> CONNECTED
control-allow-user-only(no deny)     host=localhost  -> CONNECTED
control-allow-user-only(no deny)     host=127.0.0.1  -> CONNECTED
control-allow-user-only(no deny)     host=OJESH      -> CONNECTED
deny-network+allow-user              host=.          -> CONNECTED
deny-network+allow-user              host=localhost  -> CONNECTED
deny-network+allow-user              host=127.0.0.1  -> CONNECTED
deny-network+allow-user              host=OJESH      -> CONNECTED
LanmanServer=Running; shares: ADMIN$,C$,D$,IPC$; Test-Path \\localhost\IPC$ = False
```

(One `IsConnected=False` line on `.` is a race in reading the flag right after the client connected; the client connected.)

## Interpretation

- Local access works with the GlyphLens ACL (case 3, host `.`): requirement "current user can connect" is met.
- Connections addressed to `localhost`, `127.0.0.1` and the machine's own name **succeeded even with the deny entry**. Windows
  resolves a pipe path naming the local computer to a local open, with the caller's ordinary interactive token, which does not
  carry the NETWORK SID. So these attempts are not network logons and say nothing about whether the deny entry blocks one.
- The control cases behave identically to the deny case, so this experiment cannot distinguish them. A real remote
  (SMB, network-logon) client could not be produced on a single machine.

## What is and is not established

- Established: the explicit DACL contains exactly Deny NETWORK + Allow current user, and a local same-user client works
  (also covered by `NamedPipeWorkerListenerTests`, which inspects the DACL).
- NOT established: that a genuine remote client is refused. The deny entry is expected to refuse it (remote SMB connections carry the
  NETWORK SID) and .NET additionally sets PIPE_REJECT_REMOTE_CLIENTS on Windows, but neither was observed here.
  Verification needs a second machine on the network connecting to `\\<host>\pipe\glyphlens-...`.
- Same-user local processes are within the trust boundary; the session token handshake is the defence against an
  impostor of that kind.
