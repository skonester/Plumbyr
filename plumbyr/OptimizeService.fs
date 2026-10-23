namespace WindowsCleaner

// Conservative Windows 10/11 optimizations — an F# port of cleaner_optimize.py
// from Swept (https://github.com/vladcodex/swept), used under the MIT License:
//
//   MIT License
//
//   Copyright (c) 2026 vladcodex
//
//   Permission is hereby granted, free of charge, to any person obtaining a copy
//   of this software and associated documentation files (the "Software"), to deal
//   in the Software without restriction, including without limitation the rights
//   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//   copies of the Software, and to permit persons to whom the Software is
//   furnished to do so, subject to the following conditions:
//
//   The above copyright notice and this permission notice shall be included in all
//   copies or substantial portions of the Software.
//
//   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
//   SOFTWARE.
//
// Safety rules carried over from Swept: no system files, drivers or services are
// deleted; only registry values and stock tools (powercfg, Appx cmdlets) are used;
// each item runs in isolation so one failure never stops the rest.

open System
open System.IO
open System.Diagnostics
open System.Text.RegularExpressions
open Microsoft.Win32

type OptimizeGroup =
    | Basics
    | Debloat

type OptimizeOutcome =
    | Applied
    // Plumbyr always runs elevated, so a denied write means Windows itself protects
    // the value (e.g. SYSTEM-owned keys on newer builds) — retrying won't help.
    | Protected of reason: string
    | Failed of reason: string

type Optimization = {
    Key         : string
    Group       : OptimizeGroup
    Title       : string
    Description : string
    SafeDefault : bool
    Apply       : unit -> OptimizeOutcome
}

module OptimizeService =

    let groupTitle = function
        | Basics  -> "Basic settings"
        | Debloat -> "Full debloat (bare Windows)"

    // ------------------------------------------------------------------
    // Registry and process helpers
    // ------------------------------------------------------------------

    let private clip (n: int) (s: string) = if s.Length > n then s.Substring(0, n) else s

    let private protectedReason = "Windows protects this registry value"

    let private regWrite (hive: RegistryKey) (path: string) (name: string) (value: int) =
        try
            use key = hive.CreateSubKey(path, true)
            key.SetValue(name, value, RegistryValueKind.DWord)
            Applied
        with
        | :? UnauthorizedAccessException
        | :? Security.SecurityException -> Protected protectedReason
        | ex -> Failed ex.Message

    // Runs every write and merges the results; identical reasons are collapsed so
    // three identical denials don't show the same message three times.
    let private writeMany (steps: (RegistryKey * string * string * int) list) =
        let outcomes = steps |> List.map (fun (hive, path, name, value) -> regWrite hive path name value)
        let reasons pick = outcomes |> List.choose pick |> List.distinct |> String.concat "; "
        if outcomes |> List.exists (function Failed _ -> true | _ -> false) then
            Failed (reasons (function Failed r -> Some r | _ -> None))
        elif outcomes |> List.exists (function Protected _ -> true | _ -> false) then
            Protected (reasons (function Protected r -> Some r | _ -> None))
        else
            Applied

    let private systemTool (parts: string list) =
        Path.Combine(Array.ofList (Environment.SystemDirectory :: parts))

    let private powercfg   = systemTool [ "powercfg.exe" ]
    let private powershell = systemTool [ "WindowsPowerShell"; "v1.0"; "powershell.exe" ]

    let private run (exe: string) (args: string list) (timeoutMs: int) =
        try
            let psi = ProcessStartInfo(exe)
            for a in args do psi.ArgumentList.Add(a)
            psi.UseShellExecute        <- false
            psi.CreateNoWindow         <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError  <- true
            use proc   = Process.Start(psi)
            let stdout = proc.StandardOutput.ReadToEndAsync()
            let stderr = proc.StandardError.ReadToEndAsync()
            if proc.WaitForExit(timeoutMs) then
                proc.ExitCode, stdout.Result + stderr.Result
            else
                (try proc.Kill(true) with _ -> ())
                -1, sprintf "timed out after %d s" (timeoutMs / 1000)
        with ex ->
            -1, ex.Message

    let private deniedMarkers =
        [ "access is denied"; "requires elevation"; "elevated permissions"; "administrator privileges" ]

    let private utilResult (code: int, out: string) =
        if code = 0 then Applied
        else
            let text = out.Trim()
            let lower = text.ToLowerInvariant()
            if deniedMarkers |> List.exists lower.Contains then Protected "access denied"
            elif text = "" then Failed (sprintf "exit code %d" code)
            else Failed (clip 200 text)

    // ------------------------------------------------------------------
    // Windows version
    // ------------------------------------------------------------------

    /// e.g. "Windows 11 (build 26100) | edition: Professional"
    let windowsInfo () =
        try
            use key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            let build =
                match key.GetValue("CurrentBuildNumber") with
                | :? string as s -> (match Int32.TryParse s with | true, b -> b | _ -> 0)
                | _ -> 0
            let edition =
                match key.GetValue("EditionID") with
                | :? string as s when s <> "" -> s
                | _ -> "—"
            // build >= 22000 => Windows 11, otherwise Windows 10.
            let display = if build >= 22000 then "Windows 11" else "Windows 10"
            sprintf "%s (build %d) | edition: %s" display build edition
        with _ ->
            Environment.OSVersion.VersionString

    // ------------------------------------------------------------------
    // Optimizations
    // ------------------------------------------------------------------

    let private hklm = Registry.LocalMachine
    let private hkcu = Registry.CurrentUser
    let private hklmPolData = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection"
    let private hkcuCurrent = @"Software\Microsoft\Windows\CurrentVersion"

    let private optTelemetry () =
        writeMany [
            hklm, hklmPolData, "AllowTelemetry", 0
            hklm, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0
            hklm, hklmPolData, "MaxTelemetryAllowed", 0
        ]

    let private optAdvertising () =
        let cdm = hkcuCurrent + @"\ContentDeliveryManager"
        writeMany [
            hkcu, hkcuCurrent + @"\AdvertisingInfo", "Enabled", 0
            hkcu, cdm, "SoftLandingEnabled", 0
            hkcu, cdm, "SystemPaneSuggestionsEnabled", 0
            hkcu, cdm, "SubscribedContent-338389Enabled", 0
            hkcu, cdm, "SubscribedContent-310093Enabled", 0
            hkcu, cdm, "RotatingLockScreenEnabled", 0
        ]

    let private highPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"

    let private optPower () =
        let code, out = run powercfg [ "/list" ] 30000
        if code <> 0 then
            Failed "could not read the power scheme list"
        elif out.Contains(highPerformance, StringComparison.OrdinalIgnoreCase) then
            utilResult (run powercfg [ "/setactive"; highPerformance ] 30000)
        else
            // Hidden on many modern-standby machines: restore it from the template first.
            let code2, out2 = run powercfg [ "/duplicatescheme"; highPerformance ] 30000
            if code2 <> 0 then
                utilResult (code2, out2)
            else
                let m = Regex.Match(out2.ToLowerInvariant(), "([0-9a-f]{8}-[0-9a-f-]{27})")
                let scheme = if m.Success then m.Groups[1].Value else highPerformance
                utilResult (run powercfg [ "/setactive"; scheme ] 30000)

    let private optHibernate () =
        utilResult (run powercfg [ "/h"; "off" ] 30000)

    let private optBackground () =
        writeMany [
            hkcu, hkcuCurrent + @"\BackgroundAccessApplications", "GlobalUserDisabled", 1
            hkcu, hkcuCurrent + @"\Search", "BackgroundAppGlobalToggle", 0
        ]

    let private optGameDvr () =
        writeMany [
            hkcu, @"System\GameConfigStore", "GameDVR_Enabled", 0
            hkcu, hkcuCurrent + @"\GameDVR", "AppCaptureEnabled", 0
        ]

    let private optStartupDelay () =
        writeMany [ hkcu, hkcuCurrent + @"\Explorer\Serialize", "StartupDelayInMSec", 0 ]

    let private optVisualFx () =
        writeMany [ hkcu, hkcuCurrent + @"\Explorer\VisualEffects", "VisualFXSetting", 2 ]

    /// Built-in apps that are safe to remove for the current user. Apps that aren't
    /// installed are skipped; all of them can be reinstalled from the Microsoft Store.
    let bloatApps = [
        "Microsoft.BingWeather",                  "Weather"
        "Microsoft.BingNews",                     "News"
        "Microsoft.BingSports",                   "Sports"
        "Microsoft.BingFinance",                  "Finance"
        "Microsoft.GetHelp",                      "Get Help"
        "Microsoft.MicrosoftSolitaireCollection", "Solitaire Collection"
        "Microsoft.MicrosoftOfficeHub",           "Office Hub"
        "Microsoft.People",                       "People"
        "Microsoft.SkypeApp",                     "Skype"
        "Microsoft.WindowsFeedbackHub",           "Feedback Hub"
        "Microsoft.WindowsMaps",                  "Maps"
        "Microsoft.ZuneMusic",                    "Groove Music"
        "Microsoft.ZuneVideo",                    "Movies & TV"
        "king.com.CandyCrushSaga",                "Candy Crush"
    ]

    let private optDebloatApps () =
        // Package names are spliced into the script text, so only strict
        // identifiers (letters, digits, dot, dash, underscore) are allowed through.
        let names =
            bloatApps
            |> List.map fst
            |> List.filter (fun n -> Regex.IsMatch(n, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"))
        let quoted = names |> List.map (sprintf "'%s'") |> String.concat ", "
        let script =
            sprintf "$apps = @(%s); foreach ($a in $apps) { Get-AppxPackage -Name $a -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue }" quoted
        let code, out = run powershell [ "-NoProfile"; "-NonInteractive"; "-Command"; script ] 300000
        if code = 0 then Applied else Failed (clip 300 (out.Trim()))

    let private optWidgetsCopilot () =
        writeMany [
            hkcu, hkcuCurrent + @"\Explorer\Advanced", "TaskbarDa", 0
            hkcu, @"Software\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0
            hkcu, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 1
        ]

    let private optSearchSuggest () =
        writeMany [
            hkcu, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1
            hkcu, hkcuCurrent + @"\SearchSettings", "IsDynamicSearchBoxEnabled", 0
        ]

    let private optWer () =
        writeMany [ hklm, @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1 ]

    // Reversibility comes from the system restore point created before applying;
    // there is no per-item undo.
    let optimizations : Optimization list = [
        { Key = "telemetry"; Group = Basics; SafeDefault = true; Apply = optTelemetry
          Title = "Disable telemetry and diagnostics"
          Description = @"Blocks sending diagnostic data to Microsoft. (registry: DataCollection\AllowTelemetry=0)" }
        { Key = "advertising"; Group = Basics; SafeDefault = true; Apply = optAdvertising
          Title = "Disable ads, tips and suggestions"
          Description = "Removes the advertising ID, tips and promotional tiles from Start and notifications." }
        { Key = "power"; Group = Basics; SafeDefault = false; Apply = optPower
          Title = "Power plan — High performance"
          Description = "Switches the power plan to high performance (reduces desktop lag)." }
        { Key = "hibernate"; Group = Basics; SafeDefault = false; Apply = optHibernate
          Title = "Disable hibernation (free disk space)"
          Description = "Frees space by removing the hibernation file. Reversible: powercfg /h on. Note: turns off Fast Startup." }
        { Key = "background"; Group = Basics; SafeDefault = true; Apply = optBackground
          Title = "Disable background apps"
          Description = "Stops apps from running in the background (lower load)." }
        { Key = "gamedvr"; Group = Basics; SafeDefault = true; Apply = optGameDvr
          Title = "Disable Game DVR / Game Bar"
          Description = "Turns off background recording and the game overlay (lower load, shorter delays)." }
        { Key = "startup_delay"; Group = Basics; SafeDefault = true; Apply = optStartupDelay
          Title = "Remove the startup delay"
          Description = "Startup programs begin launching immediately." }
        { Key = "visualfx"; Group = Basics; SafeDefault = true; Apply = optVisualFx
          Title = "Visual effects — best performance"
          Description = "Disables interface animations and shadows. Revert via Settings > Accessibility > Visual effects." }
        { Key = "debloat_apps"; Group = Debloat; SafeDefault = false; Apply = optDebloatApps
          Title = "Remove built-in promo and game apps"
          Description = "Removes harmless preinstalled apps for the current user (Weather, News, Maps, Skype, Solitaire, Candy Crush and others). Can be reinstalled from the Microsoft Store." }
        { Key = "widgets_copilot"; Group = Debloat; SafeDefault = true; Apply = optWidgetsCopilot
          Title = "Disable widgets and Copilot on the taskbar"
          Description = "Hides the Widgets and Copilot buttons (Win11) and turns off the news feed." }
        { Key = "search_suggest"; Group = Debloat; SafeDefault = true; Apply = optSearchSuggest
          Title = "Disable web suggestions in search"
          Description = "Removes Bing suggestions and search highlights from Start and search." }
        { Key = "wer"; Group = Debloat; SafeDefault = false; Apply = optWer
          Title = "Disable Windows Error Reporting"
          Description = "Stops sending and showing error reports. Reversible." }
    ]

    /// Runs one optimization; an unexpected exception becomes a Failed outcome
    /// instead of aborting the batch.
    let apply (item: Optimization) =
        try item.Apply()
        with ex -> Failed ex.Message
