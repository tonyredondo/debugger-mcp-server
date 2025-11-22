# .NET SOS Commands Reference

A comprehensive reference of SOS (Son of Strike) debugging extension commands for .NET application analysis.

## 🔧 Setup

### Loading SOS

**WinDbg (Windows)**:
```
.loadby sos coreclr    # .NET Core / .NET 5+
.loadby sos clr        # .NET Framework
.load sos              # Generic load
```

**LLDB (Linux/macOS)**:
```
plugin load /path/to/libsosplugin.so     # Linux
plugin load /path/to/libsosplugin.dylib  # macOS
```

**Installing dotnet-sos**:
```bash
dotnet tool install -g dotnet-sos
dotnet-sos install
```

---

## 🧵 Thread Commands

| Command | Description |
|---------|-------------|
| `!threads` | List all managed threads |
| `!clrstack` | Managed call stack for current thread |
| `!clrstack -a` | Call stack with arguments and locals |
| `!clrstack -p` | Call stack with parameters |
| `!dso` | Display stack objects |
| `!dumpstack` | Raw stack dump |
| `!eestack` | Stack for all managed threads |

---

## 🚨 Exception Commands

| Command | Description |
|---------|-------------|
| `!pe` | Print current exception |
| `!pe -nested` | Print exception with inner exceptions |
| `!printexception` | Same as !pe |
| `!dumpexceptions` | List all exception objects on heap |

---

## 🧱 Heap Commands

| Command | Description |
|---------|-------------|
| `!dumpheap` | Dump entire heap |
| `!dumpheap -stat` | Heap statistics (type counts/sizes) |
| `!dumpheap -type TypeName` | Find objects by type name |
| `!dumpheap -mt MethodTable` | Find objects by method table |
| `!dumpheap -min size` | Objects larger than size |
| `!dumpheap -max size` | Objects smaller than size |
| `!dumpheap -strings` | Dump all strings |
| `!gcroot address` | Find GC roots for object |
| `!objsize address` | Calculate retained object size |
| `!dumpobj address` | Dump object details |
| `!do address` | Short for !dumpobj |
| `!dumparray address` | Dump array contents |
| `!da address` | Short for !dumparray |

---

## 📊 Type Commands

| Command | Description |
|---------|-------------|
| `!dumpmt MethodTable` | Dump method table |
| `!dumpmt -md MethodTable` | Method table with method descs |
| `!dumpclass address` | Dump class (EEClass) info |
| `!dumpmd MethodDesc` | Dump method descriptor |
| `!dumpdomain` | List all AppDomains |
| `!dumpmodule address` | Dump module info |
| `!dumpil MethodDesc` | Dump IL code |
| `!name2ee module!type` | Find type by name |
| `!token2ee module token` | Find by metadata token |

---

## 🗑️ GC Commands

| Command | Description |
|---------|-------------|
| `!eeheap` | Display managed heap info |
| `!eeheap -gc` | GC heap info |
| `!eeheap -loader` | Loader heap info |
| `!gchandles` | Display all GC handles |
| `!gchandleleaks` | Potential GC handle leaks |
| `!finalizequeue` | Finalizer queue contents |
| `!fq` | Short for !finalizequeue |
| `!gcwhere address` | Find object's GC generation |
| `!verifyheap` | Verify GC heap integrity |
| `!dumpgen N` | Dump objects in generation N |

---

## 🔒 Synchronization Commands

| Command | Description |
|---------|-------------|
| `!syncblk` | Display sync blocks |
| `!syncblk -all` | All sync blocks |
| `!rwlock address` | Reader-writer lock info |
| `!dumpasync` | Async state machines |
| `!dumpasync -mt` | Async by method table |
| `!dumpasync -tasks` | Show task states |
| `!threadpool` | Thread pool info |

---

## 📈 CLR Info Commands

| Command | Description |
|---------|-------------|
| `!eeversion` | CLR version |
| `!clrmodules` | List CLR modules |
| `!bpmd module method` | Set managed breakpoint |
| `!u MethodDesc` | Disassemble managed method |
| `!ip2md address` | Find MethodDesc for IP |

---

## 🔍 Memory Analysis Commands

| Command | Description |
|---------|-------------|
| `!analyzeoom` | Analyze out-of-memory |
| `!dumplog` | Dump stress log |
| `!histstats` | Histogram of object sizes |
| `!histinit` | Initialize histogram |
| `!histobj address` | Object history |
| `!histroot address` | Root history |

---

## 💡 Common Workflows

### Memory Leak Investigation
```
!dumpheap -stat
!dumpheap -type SuspectedType
!gcroot address
!objsize address
```

### Exception Analysis
```
!pe -nested
!threads
!clrstack -a
!dso
```

### Deadlock Detection
```
!syncblk
!threads
!clrstack (for each blocked thread)
!dumpasync
```

### .NET Memory Usage
```
!eeheap -gc
!dumpheap -stat
!finalizequeue
!gchandles
```

### Async/Await Issues
```
!dumpasync
!dumpasync -tasks
!threads
!clrstack -a
```

---

## 🔗 Quick Reference

### Top 10 Commands for Crash Analysis
1. `!threads` - See all managed threads
2. `!pe` - Print current exception
3. `!clrstack` - Managed call stack
4. `!dso` - Stack objects
5. `!dumpheap -stat` - Heap overview
6. `!gcroot address` - Find object roots
7. `!dumpobj address` - Object details
8. `!syncblk` - Sync block info
9. `!dumpasync` - Async state machines
10. `!eeversion` - CLR version

### Memory Leak Pattern
```
# 1. Get heap statistics
!dumpheap -stat

# 2. Find suspicious type
!dumpheap -type MyNamespace.LeakingClass

# 3. Pick an instance
!gcroot 0x12345678

# 4. Analyze roots
```

### Deadlock Pattern
```
# 1. Check sync blocks
!syncblk

# 2. List threads and their states
!threads

# 3. Check each thread's stack
!clrstack

# 4. Check async state
!dumpasync
```

---

## ⚠️ Platform Notes

### Windows (WinDbg)
- Use `!soshelp` for command help
- Commands work with both .NET Framework and .NET Core

### Linux/macOS (LLDB)
- Some commands may have slightly different syntax
- Use `soshelp` (without !) after loading plugin
- Ensure libsosplugin matches CLR version

### Finding SOS Plugin Path
```bash
# Linux
find /usr -name "libsosplugin.so" 2>/dev/null

# macOS
find /usr/local -name "libsosplugin.dylib" 2>/dev/null

# Or use dotnet-sos
dotnet-sos install
```

