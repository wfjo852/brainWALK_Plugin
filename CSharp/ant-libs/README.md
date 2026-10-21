The supplied `ANT_Libraries.zip` contains the desktop PC SDK inside a nested ZIP. Run `CSharp/build.ps1` to extract these x86 binaries into `pc/`:

- `ANT_NET.dll` (assembly namespace `ANT_Managed_Library`)
- `ANT_WrappedLib.dll`
- Any additional native DLLs required by that exact library package

The project copies only `pc/*.dll` to the output directory. The `ARM/` and `x64/` folders contain UWP components and are not compatible with this WinForms build.
