# WinDbg Commands Reference

A comprehensive reference of WinDbg commands for crash analysis and debugging on Windows.

## 📋 Basic Commands

| Command | Description |
|---------|-------------|
| `k` | Display call stack |
| `kp` | Display call stack with parameters |
| `kb` | Display call stack with first three parameters |
| `kn` | Display call stack with frame numbers |
| `kv` | Display call stack with FPO data |
| `~` | List all threads |
| `~*k` | Show call stack for all threads |
| `~Ns` | Switch to thread N |
| `!analyze -v` | Automated crash analysis (verbose) |
| `!analyze` | Automated crash analysis (brief) |
| `r` | Display registers |
| `lm` | List loaded modules |
| `lmvm module` | Verbose module info |
| `x module!*` | List symbols in module |
| `vertarget` | Show target system info |

## 💾 Memory Commands

| Command | Description |
|---------|-------------|
| `dd address` | Display DWORD (4-byte) values |
| `dq address` | Display QWORD (8-byte) values |
| `dp address` | Display pointer-sized values |
| `da address` | Display ASCII string |
| `du address` | Display Unicode string |
| `db address` | Display bytes |
| `dps address` | Display pointer-sized with symbols |
| `!address` | Display memory regions |
| `!address address` | Info about specific address |
| `!vprot address` | Virtual memory protection |
| `s -a start end "string"` | Search for ASCII string |
| `s -u start end "string"` | Search for Unicode string |

## 🐛 Debugging Commands

| Command | Description |
|---------|-------------|
| `.ecxr` | Switch to exception context record |
| `.exr -1` | Display most recent exception record |
| `.exr address` | Display exception record at address |
| `!peb` | Display process environment block |
| `!teb` | Display thread environment block |
| `!error code` | Decode error code |
| `.lastevent` | Show last debug event |
| `.eventlog` | Display event log |

## 🔍 Symbol Commands

| Command | Description |
|---------|-------------|
| `.sympath` | Display/set symbol path |
| `.sympath+ path` | Add to symbol path |
| `.symfix` | Set to Microsoft symbol server |
| `.symfix+ path` | Add local cache to MS symbols |
| `.reload` | Reload symbols |
| `.reload /f module` | Force reload specific module |
| `!sym noisy` | Enable verbose symbol loading |
| `!sym quiet` | Disable verbose symbol loading |
| `ln address` | List nearest symbols |
| `uf function` | Unassemble function |

## 🧱 Heap Commands

| Command | Description |
|---------|-------------|
| `!heap -s` | Heap summary |
| `!heap -stat` | Heap statistics |
| `!heap -a address` | Analyze specific heap |
| `!heap -flt s size` | Find allocations of specific size |
| `!heap -l` | Detect heap leaks |
| `!heap -p -a address` | Page heap info for address |
| `!address -summary` | Memory usage summary |

## 🔒 Lock Commands

| Command | Description |
|---------|-------------|
| `!locks` | Display critical sections |
| `!cs -l` | List locked critical sections |
| `!cs address` | Info about specific critical section |
| `!runaway` | Display thread CPU times |
| `!runaway 7` | All thread time categories |
| `!deadlock` | Detect potential deadlocks |

## 📊 Handle Commands

| Command | Description |
|---------|-------------|
| `!handle` | Display handles |
| `!handle 0 f` | All handles with types |
| `!handle handle f` | Specific handle details |
| `!htrace -enable` | Enable handle tracing |
| `!htrace handle` | Handle trace for specific handle |

## 🔧 Extension Commands

| Command | Description |
|---------|-------------|
| `.load extension` | Load extension DLL |
| `.unload extension` | Unload extension |
| `.chain` | List loaded extensions |
| `.loadby sos coreclr` | Load SOS for .NET Core |
| `.loadby sos clr` | Load SOS for .NET Framework |

## 💡 Tips

### Quick Crash Analysis
```
!analyze -v
.ecxr
k
~*k
```

### Memory Investigation
```
!address -summary
!heap -s
!heap -stat
```

### Thread Analysis
```
~*k
!runaway 7
!locks
```

### Symbol Troubleshooting
```
!sym noisy
.reload /f
.sympath
ln address
```

## 🔗 Common Scenarios

### Access Violation Analysis
```
!analyze -v
.ecxr
r
db poi(@rsp)
k
```

### Stack Overflow
```
!analyze -v
~*k
!teb
```

### Hang/Deadlock
```
!locks
~*k
!runaway 7
!analyze -hang
```

### Memory Leak
```
!heap -l
!heap -stat
!address -summary
```

