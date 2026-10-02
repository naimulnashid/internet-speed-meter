' Launches the dashboard with no window at all.
'
' This wrapper exists because there is no reliable way to start a console
' program from Task Scheduler without a window flashing up: the task's own
' "Hidden" checkbox does not suppress it, and powershell -WindowStyle Hidden
' still blinks. WScript.Shell's Run with intWindowStyle = 0 genuinely does not
' create one.
'
' Double-clicking this file also works, if you want the dashboard up before the
' next logon.
'
' Pass -Lan (wscript scripts\dashboard-hidden.vbs -Lan) to listen on every
' network interface, so a phone on the same Wi-Fi can open it. It is passed
' straight through to dashboard-service.ps1, which checks for a password first.

Dim shell, fso, here, ps1, flags, i
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

here = fso.GetParentFolderName(WScript.ScriptFullName)
ps1 = fso.BuildPath(here, "dashboard-service.ps1")

flags = ""
For i = 0 To WScript.Arguments.Count - 1
    If LCase(WScript.Arguments(i)) = "-lan" Then flags = " -Lan"
Next

' 0 = hidden window, False = do not wait for it to finish.
shell.Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & ps1 & """" & flags, 0, False
