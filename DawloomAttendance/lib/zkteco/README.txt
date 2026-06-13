ZKTeco SDK files go in this folder.

Required DLLs (download from https://www.zkteco.com/en/SDK - Standalone SDK):
  - zkemkeeper.dll          (the 32-bit COM ActiveX)
  - commpro.dll
  - comms.dll
  - plcommpro.dll
  - tcpcomm.dll
  - rscagent.dll
  - rscomm.dll

After dropping the DLLs here:
  1. Right-click Register.bat -> Run as Administrator.
  2. In Visual Studio, right-click DawloomAttendance project ->
     Add -> Reference -> COM -> tick "ZKEMKeeper 1.0 Type Library" -> OK.
     This auto-generates Interop.zkemkeeper.dll.
  3. Ensure project Platform target is x86 (already set in csproj).
  4. Build and run.
