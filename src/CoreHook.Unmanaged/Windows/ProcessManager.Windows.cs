﻿using System;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace CoreHook.Unmanaged.Windows
{
    public sealed partial class ProcessManager : IProcessManager
    {
        public Process ProcessHandle { get; private set; }

        public ProcessManager(Process process)
        {
            ProcessHandle = process;
        }

        public void OpenHandle(Process process)
        {
            ProcessHandle?.Dispose();
            ProcessHandle = process;
        }

        private static SafeProcessHandle GetProcessHandle(int processId, int access)
        { 
            SafeProcessHandle handle = Interop.Kernel32.OpenProcess(access, false, processId);

            if (handle == null)
            {
                throw new UnauthorizedAccessException("Failed to open process with query access.");
            }

            return handle;
        }

        private static SafeProcessHandle GetReadProcessHandle(int processId)
        {
            return GetProcessHandle(processId,
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_READ);
        }

        private IntPtr GetWin32ProcAddress(string module, string function)
        {
            if (Environment.Is64BitOperatingSystem)
            {
                if (ProcessHandle.Is64Bit())
                {
                    if (!Environment.Is64BitProcess)
                    {
                        throw new InvalidOperationException(
                            "Cannot open a handle to a 64-bit proc address from a 32-bit process."
                            );
                    }
                }
            }

            return GetAbsoluteFunctionAddressEx(module, function);
        }

        public void InjectBinary(string modulePath)
        {
            using (var hProcess = GetProcessHandle(ProcessHandle.Id,
                Interop.Advapi32.ProcessOptions.PROCESS_CREATE_THREAD |
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_OPERATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_READ |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_WRITE))
            {
                var pathBytes = Encoding.Unicode.GetBytes(modulePath + "\0");

                // Allocate space in the remote process for the DLL path.
                var remoteAllocAddr = Interop.Kernel32.VirtualAllocEx(
                    hProcess,
                    IntPtr.Zero,
                    new UIntPtr((uint)pathBytes.Length),
                    Interop.Kernel32.AllocationType.Commit | Interop.Kernel32.AllocationType.Reserve,
                    Interop.Kernel32.MemoryProtection.ReadWrite);

                if (remoteAllocAddr == IntPtr.Zero)
                {
                    throw new Win32Exception("Failed to allocate memory in remote process.");
                }

                try
                {
                    // Write the DLL path to the allocated memory.
                    var result = Interop.Kernel32.WriteProcessMemory(
                        hProcess,
                        remoteAllocAddr,
                        pathBytes,
                        pathBytes.Length,
                        out IntPtr bytesWritten);

                    if (!result || bytesWritten.ToInt32() != pathBytes.Length)
                    {
                        throw new Win32Exception("Failed to allocate memory in remote process.");
                    }

                    // Create a thread in the process at LoadLibraryW and pass it the DLL path.
                    var hThread = Interop.Kernel32.CreateRemoteThread(
                     hProcess,
                     IntPtr.Zero,
                     UIntPtr.Zero,
                     GetWin32ProcAddress(
                         Path.Combine(
                             Environment.ExpandEnvironmentVariables("%Windir%"),
                             Environment.Is64BitOperatingSystem ? "System32" : "SysWOW64",
                             "kernel32.dll"
                             ), "LoadLibraryW"),
                         remoteAllocAddr,
                         0,
                         IntPtr.Zero);

                    if (hThread.DangerousGetHandle() == IntPtr.Zero)
                    {
                        throw new Win32Exception("Failed to create thread in remote process.");
                    }

                    const int infiniteWait = -1;
                    Interop.Kernel32.WaitForSingleObject(
                        hThread,
                        infiniteWait);

                    Interop.Kernel32.CloseHandle(hThread.DangerousGetHandle());
                }
                finally
                {
                    Interop.Kernel32.VirtualFreeEx(
                        hProcess,
                        remoteAllocAddr,
                        new UIntPtr(0),
                        Interop.Kernel32.FreeType.Release);
                }
            }
        }

        /// <summary>
        /// Execute function inside the specified module with custom arguments.
        /// </summary>
        /// <param name="module">The name of the module containing the desired function.</param>
        /// <param name="function">The name of the exported function we will call.</param>
        /// <param name="arguments">Serialized arguments for passing to the module function.</param>
        /// <param name="canWait">We can wait for the thread to finish before cleaning up memory
        /// or we need to cleanup later.</param>
        public IntPtr Execute(string module, string function, byte[] arguments, bool canWait = true)
        {
            using (var hProcess = GetProcessHandle(ProcessHandle.Id,
                Interop.Advapi32.ProcessOptions.PROCESS_CREATE_THREAD |
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_OPERATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_READ |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_WRITE))
            {

                // Allocate space in the remote process for the DLL path.
                IntPtr remoteAllocAddr = Interop.Kernel32.VirtualAllocEx(
                    hProcess,
                    IntPtr.Zero,
                    new UIntPtr((uint)arguments.Length),
                    Interop.Kernel32.AllocationType.Commit | Interop.Kernel32.AllocationType.Reserve,
                    Interop.Kernel32.MemoryProtection.ReadWrite);

                if (remoteAllocAddr == IntPtr.Zero)
                {
                    throw new Win32Exception("Failed to allocate memory in remote process.");
                }

                try
                {
                    // Write the DLL path to the allocated memory.
                    bool result = Interop.Kernel32.WriteProcessMemory(
                        hProcess,
                        remoteAllocAddr,
                        arguments,
                        arguments.Length,
                        out IntPtr bytesWritten);

                    if (!result || bytesWritten.ToInt32() != arguments.Length)
                    {
                        throw new Win32Exception("Failed to allocate memory in remote process.");
                    }

                    // Create a thread in the process at LoadLibraryW and pass it the DLL path.
                    var hThread = Interop.Kernel32.CreateRemoteThread(
                        hProcess,
                        IntPtr.Zero,
                        UIntPtr.Zero,
                        GetAbsoluteFunctionAddressEx(module, function),
                        remoteAllocAddr,
                        0,
                        IntPtr.Zero);

                    if (hThread.DangerousGetHandle() == IntPtr.Zero)
                    {
                        throw new Win32Exception("Failed to create thread in remote process.");
                    }

                    if (canWait)
                    {
                        const int infiniteWait = -1;
                        Interop.Kernel32.WaitForSingleObject(
                            hThread,
                            infiniteWait);
                    }

                    Interop.Kernel32.CloseHandle(hThread.DangerousGetHandle());

                    return remoteAllocAddr;
                }
                finally
                {
                    if (canWait)
                    {
                        Interop.Kernel32.VirtualFreeEx(
                            hProcess,
                            remoteAllocAddr,
                            new UIntPtr(0),
                            Interop.Kernel32.FreeType.Release);
                    }
                }
            }
        }

        private IntPtr MemAllocate(int size)
        {
            using (var hProcess = GetProcessHandle(ProcessHandle.Id,
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_OPERATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_READ |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_WRITE))
            {
                // Allocate space in the remote process for the DLL path.

                IntPtr remoteAllocAddr = Interop.Kernel32.VirtualAllocEx(
                    hProcess,
                    IntPtr.Zero,
                    new UIntPtr((uint)size),
                    Interop.Kernel32.AllocationType.Commit | Interop.Kernel32.AllocationType.Reserve,
                    Interop.Kernel32.MemoryProtection.ReadWrite);

                if (remoteAllocAddr == IntPtr.Zero)
                {
                    throw new Win32Exception("Failed to allocate memory in remote process.");
                }

                return remoteAllocAddr;
            }
        }

        public IntPtr MemCopyTo(byte[] data, int? size)
        {
            using (var hProcess = GetProcessHandle(ProcessHandle.Id,
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_OPERATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_READ |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_WRITE))
            {
                int dataLen = size ?? data.Length;
                IntPtr remoteAllocAddr = MemAllocate(dataLen);

                // Write the DLL path to the allocated memory.
                bool result = Interop.Kernel32.WriteProcessMemory(
                    hProcess,
                    remoteAllocAddr,
                    data,
                    dataLen,
                    out IntPtr bytesWritten);

                if (!result || bytesWritten.ToInt32() != dataLen)
                {
                    throw new Win32Exception("Failed to allocate memory in remote process.");
                }
                return remoteAllocAddr;
            }
        }

        public bool FreeMemory(IntPtr address, int? size)
        {
            if (address == IntPtr.Zero)
                return true;

            using (var hProcess = GetProcessHandle(ProcessHandle.Id,
                Interop.Advapi32.ProcessOptions.PROCESS_QUERY_INFORMATION |
                Interop.Advapi32.ProcessOptions.PROCESS_VM_OPERATION))
            {
                return size.HasValue ?
                    Interop.Kernel32.VirtualFreeEx(
                        hProcess, 
                        address, 
                        new UIntPtr((uint)size.GetValueOrDefault()),
                        Interop.Kernel32.FreeType.Decommit) :
                    Interop.Kernel32.VirtualFreeEx(
                        hProcess, 
                        address, 
                        new UIntPtr(0), 
                        Interop.Kernel32.FreeType.Release);
            }
        }

        private IntPtr GetAbsoluteFunctionAddressEx(string moduleFileName, string functionName)
        {
            var hProcess = GetReadProcessHandle(ProcessHandle.Id);

            IntPtr hModule = GetModuleHandleByFileName(hProcess, moduleFileName);

            if (hModule == IntPtr.Zero)
            {
                throw new ModuleNotFoundException("Module not found in process.");
            }

            return GetAbsoluteFunctionAddress(hProcess, hModule, functionName);
        }

        private IntPtr GetAbsoluteFunctionAddress(SafeProcessHandle hProcess, IntPtr hModule, string functionName)
        {
            var moduleInfo = GetModuleInfo(hProcess, hModule);

            DataDirectory exportDir = GetDataDirectory(ReadPage(hProcess, moduleInfo.BaseOfDll), 0);

            var buffer = new byte[exportDir.Size];

            IntPtr bytesRead;

            if (!NativeMethods.ReadProcessMemory(
                hProcess,
                moduleInfo.BaseOfDll + (int)exportDir.Rva,
                buffer,
                buffer.Length,
                out bytesRead) || bytesRead != (IntPtr)buffer.Length)
            {
                throw new Win32Exception("Failed to read export table from memory of module.");
            }

            return new IntPtr(moduleInfo.BaseOfDll.ToInt64() +
                GetFunctionAddress(buffer, exportDir.Rva, functionName).ToInt64());
        }

        private Interop.Kernel32.NtModuleInfo GetModuleInfo(SafeProcessHandle hProcess, IntPtr hModule)
        {
            if (!Interop.Kernel32.GetModuleInformation(
                hProcess,
                hModule,
                out Interop.Kernel32.NtModuleInfo moduleInfo))
            {
                throw new Win32Exception("Failed to get module information.");
            }

            return moduleInfo;
        }

        private byte[] ReadPage(SafeProcessHandle hProcess, IntPtr address)
        {
            var buffer = new byte[Environment.SystemPageSize];

            if (!NativeMethods.ReadProcessMemory(
                hProcess,
                address,
                buffer,
                buffer.Length,
                out IntPtr bytesRead) || bytesRead != (IntPtr)buffer.Length)
            {
                throw new Win32Exception("Failed to read PE header from memory of module.");
            }

            return buffer;
        }

        private DataDirectory GetDataDirectory(byte[] peHeader, int index)
        {
            using (var io = new BinaryReader(new MemoryStream(peHeader)))
            {
                io.BaseStream.Position = 0x3c;

                // Skip to the PE header.
                io.BaseStream.Position = io.ReadInt32();

                // Check for the PE\0\0 magic.
                if (io.ReadUInt32() != 0x00004550)
                {
                    throw new Win32Exception("Invalid PE header in module.");
                }

                // Skip the COFF header.
                io.BaseStream.Position += 20;

                // Read the optional header magic and skip to the directory listing.
                switch (io.ReadUInt16())
                {
                    // 32-bit application
                    case 0x10b:
                        io.BaseStream.Position += 94;
                        break;

                    // 64-bit application
                    case 0x20b:
                        io.BaseStream.Position += 110;
                        break;

                    default:
                        throw new BadImageFormatException("Unknown optional header magic in PE header.");
                }

                io.BaseStream.Position += 8 * index;

                var rva = io.ReadUInt32();

                var dir = new DataDirectory(rva, io.ReadUInt32());

                io.Close();

                return dir;
            }
        }

        private IntPtr GetFunctionAddress(byte[] exportTable, uint exportTableRva, string functionName)
        {
            var ms = new MemoryStream(exportTable);

            using (var io = new BinaryReader(ms))
            {
                // Skip flags, timestamp, version, and name of DLL RVA.
                ms.Position = 16;

                var ordinalBase = io.ReadInt32();

                var addressTableEntryCount = io.ReadUInt32();
                var namePointerTableEntryCount = io.ReadUInt32();
                var exportAddressTableRva = io.ReadUInt32() - exportTableRva;
                var exportNamePointerTableRva = io.ReadUInt32() - exportTableRva;
                var ordinalTableRva = io.ReadUInt32() - exportTableRva;

                ms.Position = exportNamePointerTableRva;

                var ret = IntPtr.Zero;
                int x;

                for (x = 0; x < namePointerTableEntryCount; x++)
                {
                    ms.Position = exportNamePointerTableRva + (x * 4);

                    var nameRva = io.ReadUInt32();

                    if (nameRva == 0)
                    {
                        continue;
                    }

                    ms.Position = nameRva - exportTableRva;

                    if (functionName == ReadNullTerminatedAsciiString(io))
                    {
                        ms.Position = ordinalTableRva + (x * 2);
                        var ordinal = io.ReadUInt16(); // + ordinalBase;

                        if (ordinal >= addressTableEntryCount)
                        {
                            throw new Win32Exception("Corrupted export table in module.");
                        }

                        ms.Position = exportAddressTableRva + (ordinal * 4); // (ordinal - ordinalBase)

                        ret = (IntPtr)io.ReadUInt32();
                    }
                }

                return ret;
            }
        }

        private string ReadNullTerminatedAsciiString(BinaryReader reader)
        {
            var sb = new StringBuilder();

            byte b;
            while ((b = reader.ReadByte()) != 0x00)
            {
                sb.Append((char)b);
            }

            return sb.ToString();
        }

        private IntPtr GetModuleHandleByFileName(SafeProcessHandle hProcess, string moduleName)
        {
            IntPtr[] handles = GetAllModuleHandles(hProcess);
            char[] chars = new char[1024];

            foreach (IntPtr moduleHandle in handles)
            {
                int length = Interop.Kernel32.GetModuleFileNameEx(hProcess, moduleHandle, chars, chars.Length);
                if(length == 0)
                {
                    continue;
                }

                var moduleFileName = (length >= 4 && chars[0] == '\\' && chars[1] == '\\' && chars[2] == '?' && chars[3] == '\\') ?
                        new string(chars, 4, length - 4) :
                        new string(chars, 0, length);

                if (length == moduleName.Length)
                {
                    if (moduleName.Equals(moduleFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        return moduleHandle;
                    }
                }

                Debug.WriteLine(moduleFileName);
            }

            return IntPtr.Zero;
        }

        private IntPtr[] GetAllModuleHandles(SafeProcessHandle hProcess)
        {
            var moduleHandles = new IntPtr[64];

            uint size;

            for (; ; )
            {
                var gcHandle = GCHandle.Alloc(moduleHandles, GCHandleType.Pinned);
                
               if(!NativeMethods.EnumProcessModulesEx(
                    hProcess,
                    gcHandle.AddrOfPinnedObject(),
                    (uint)(IntPtr.Size * moduleHandles.Length),
                    out size,
                    NativeMethods.ModuleFilterFlags.All))
                {
                    throw new Win32Exception("EnumProcessModulesEx failed");
                }

                gcHandle.Free();

                size /= (uint)IntPtr.Size;

                if (size <= moduleHandles.Length)
                {
                    break;
                }

                moduleHandles = new IntPtr[moduleHandles.Length * 2];
            }

            if (moduleHandles.Length != size)
            {
                Array.Resize(ref moduleHandles, (int)size);
            }

            return moduleHandles;
        }

        private struct DataDirectory
        {
            public readonly uint Rva;

            public readonly uint Size;

            public DataDirectory(uint rva, uint size)
            {
                Rva = rva;
                Size = size;
            }
        }
    }
}
