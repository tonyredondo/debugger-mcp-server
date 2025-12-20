# LLDB Commands Reference

A comprehensive reference of LLDB commands for crash analysis and debugging on macOS and Linux.

## 📋 Basic Commands

| Command | Description |
|---------|-------------|
| `bt` | Display backtrace (call stack) |
| `bt all` | Display backtrace for all threads |
| `bt 10` | Show top 10 frames |
| `thread list` | List all threads |
| `thread select N` | Switch to thread N |
| `thread info` | Current thread info |
| `frame select N` | Select stack frame N |
| `frame info` | Current frame info |
| `frame variable` | Show local variables |
| `register read` | Display all registers |
| `register read rax rbx` | Display specific registers |
| `image list` | List loaded modules/images |
| `target show` | Show target info |
| `platform status` | Show platform info |

## 💾 Memory Commands

| Command | Description |
|---------|-------------|
| `memory read address` | Read memory at address |
| `memory read -s4 address` | Read as 4-byte values |
| `memory read -s8 address` | Read as 8-byte values |
| `memory read -fx address` | Read as hex |
| `memory read -c 100 address` | Read 100 bytes |
| `x/10xg address` | Examine 10 giant (8-byte) hex values |
| `x/20xw address` | Examine 20 word (4-byte) hex values |
| `x/s address` | Examine as string |
| `memory region address` | Info about memory region |
| `memory region --all` | Show all memory regions |
| `memory find -s "string" start end` | Search for string |

## 🔍 Expression Commands

| Command | Description |
|---------|-------------|
| `p variable` | Print variable value |
| `p/x value` | Print in hex format |
| `p/d value` | Print in decimal format |
| `p/t value` | Print in binary format |
| `expr expression` | Evaluate expression |
| `po object` | Print object (calls description) |
| `type lookup TypeName` | Look up type info |

## 🔧 Symbol Commands

| Command | Description |
|---------|-------------|
| `target symbols add path` | Add symbol file |
| `image lookup -n function` | Find function by name |
| `image lookup -a address` | Find symbol at address |
| `image lookup -t TypeName` | Find type by name |
| `image dump symtab module` | Dump symbol table |
| `settings set target.source-map old new` | Map source paths |
| `settings show target.source-map` | Show source mappings |

## 📊 Thread Commands

| Command | Description |
|---------|-------------|
| `thread list` | List all threads |
| `thread select N` | Switch to thread N |
| `thread backtrace` | Current thread backtrace |
| `thread backtrace all` | All threads backtrace |
| `thread info` | Current thread info |
| `thread return` | Return from current frame |

## 🔒 Watchpoint/Breakpoint (Live Debugging)

| Command | Description |
|---------|-------------|
| `breakpoint list` | List breakpoints |
| `breakpoint set -n function` | Break on function |
| `breakpoint set -a address` | Break on address |
| `breakpoint delete N` | Delete breakpoint N |
| `watchpoint set variable var` | Watch variable |
| `watchpoint set expression -w write -- address` | Watch memory |
| `watchpoint list` | List watchpoints |

## 🛠️ Process/Target Commands

| Command | Description |
|---------|-------------|
| `process status` | Show process status |
| `process info` | Detailed process info |
| `target modules list` | List modules |
| `target select N` | Switch target |
| `platform status` | Platform info |

## 📁 Source Commands

| Command | Description |
|---------|-------------|
| `source list` | List source at current location |
| `source list -f file -l line` | List source at file:line |
| `source info` | Current source info |

## 🔌 Plugin Commands (SOS for .NET)

After loading SOS plugin:

| Command | Description |
|---------|-------------|
| `clrthreads` | List managed threads |
| `clrstack` | Managed call stack |
| `dso` | Display stack objects |
| `pe` | Print exception |
| `dumpheap -stat` | Heap statistics |

To load SOS:
```
plugin load /path/to/libsosplugin.so    # Linux
plugin load /path/to/libsosplugin.dylib  # macOS
```

Notes:
- When using this repository’s MCP `exec` tool, you may issue WinDbg-style SOS commands (prefixed with `!`) even on LLDB (the server strips the leading `!` for LLDB sessions). Example: `exec(command: "!dumpheap -stat")`.
- Some environments also support `sos <command>` (e.g., `sos help`, `sos dumpil ...`). If a command is “not a valid command”, try the non-`sos` form or consult `sos help`.

## 💡 Tips

### Quick Crash Analysis
```
bt
thread list
bt all
register read
```

### Memory Investigation
```
memory region --all
memory read -c 100 address
x/20xg address
```

### Thread Analysis
```
thread list
thread backtrace all
thread info
```

### Symbol Troubleshooting
```
target symbols add /path/to/symbols
image lookup -a address
image list
```

## 🔗 Common Scenarios

### Crash Analysis
```
bt
frame select 0
frame variable
register read
memory read $rsp
```

### Finding Symbols
```
image lookup -n functionName
image lookup -a 0x12345678
image dump symtab moduleName
```

### Thread Investigation
```
thread list
thread select 1
bt
frame select 0
frame variable
```

### Memory Examination
```
memory region --all
memory read -fx -c 64 address
x/10xg address
```

## 🔄 LLDB vs WinDbg Command Mapping

| WinDbg | LLDB | Description |
|--------|------|-------------|
| `k` | `bt` | Call stack |
| `~*k` | `bt all` | All threads call stack |
| `~` | `thread list` | List threads |
| `r` | `register read` | Show registers |
| `lm` | `image list` | List modules |
| `dd` | `memory read -s4` | Read DWORDs |
| `dq` | `memory read -s8` | Read QWORDs |
| `da` | `x/s` | Display ASCII string |
| `.ecxr` | (N/A for dumps) | Exception context |
