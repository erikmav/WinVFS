This project provides a .NET library to allow creating a virtual filesystem (VFS) within a directory on Windows. A sample service executable allows presenting the contents of one directory tree under a virtual directory. The library uses the ProjFS feature on Windows to progressively render the virtual filesystem within the root directory.

The VFS requires the service process to be running at all times to accept kernel requests for information about the VFS contents. For a production system this would mean starting the service process automatically on Windows start, typically by running the executable as a Windows service (the equivalent of a daemon on *nix).

## `IVfsCallbacks` Plugins
[IVfsCallbacks.cs](./Lib/IVfsCallbacks.cs) contains the interface a VFS plugin would use to answer questions from the library. It explicitly implements a two-phase lookup for directory metadata with the assumption of a fast local cache of directory information and a slower async call to retrieve directory information from a remote source like a cloud service. Retrieving a file is always assumed to be async. There are two sample implementations of this interface, one in [WinVFSServiceProgram.cs](./Service/WinVFSServiceProgram.cs) that mirrors a source directory tree into the VFS root, and another in [VfsIntegrationTests.cs](./Tests/VfsIntegrationTests.cs) used for verifying behavior.

## ProjFS Installation
Use of the library and sample service require turning on the `Windows Projected File System` Windows feature to install the `PrjFlt` kernel driver and the related user-mode API library. You can install either through the Windows Features dialog box, or in an elevated/admin PowerShell with the following command:

```powershell
enable-WindowsOptionalFeature -online -featurename client-projfs
```

A reboot may be required. If you are automating installation of multiple features you can delay reboot until later with a pattern like this:

```powershell
$rebootRequired = $false

Write-Host "Enabling ProjFS..."
$enableProjFsResult = enable-WindowsOptionalFeature -online -featurename client-projfs -NoRestart
if ($enableProjFsResult.RestartNeeded) {
    $rebootRequired = $true
}

# More logic here...

if ($rebootRequired) {
    # Reboot 
}
```

## Use with Dev Drive
[Dev Drive](https://aka.ms/DevDrive) is a new Windows feature that provides a ReFS filesystem that defaults to disallowing all filter drivers, including PrjFlt needed for ProjFS, to provide faster performance and enable [copy-on-write](https://aka.ms/EngMSDevDrive) capabilities. To use this library with a virtual root directory on a Dev Drive, Dev Drive configuration must be updated to allow PrjFlt. The command below must be run within an elevated/admin console.

```powershell
fsutil devdrv query
# Take note of the current allow-list of filters in the query above, then add PrjFlt to the list:
fsutil devdrv SetFiltersAllowed PrjFlt,<other filters comma delimited>
# Dismount the Dev Drive so that on re-access it will mount with the filter update.
fsutil volume dismount <dev drive letter>:
```
