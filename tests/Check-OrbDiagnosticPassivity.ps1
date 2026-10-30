param(
    [string]$PluginDll = "$PSScriptRoot\..\bin\Debug\FeedLikeGrandma.dll",
    [string]$ManagedPath = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed',
    [string]$BepInExCore = 'C:\Program Files (x86)\Steam\steamapps\common\Valheim\BepInEx\core',
    [string]$CecilPath = "$env:USERPROFILE\.nuget\packages\mono.cecil\0.11.6\lib\netstandard2.0\Mono.Cecil.dll"
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilPath
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$resolver.AddSearchDirectory($BepInExCore)
$resolver.AddSearchDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($PluginDll))))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($PluginDll), $reader)
function AllTypes($types) { foreach ($type in $types) { $type; AllTypes $type.NestedTypes } }
function Require($condition, [string]$message) { if (-not $condition) { throw $message } }

try {
    $types = @(AllTypes $assembly.MainModule.Types)
    $diagnosticTypes = @($types | Where-Object { $_.FullName.StartsWith('FeedLikeGrandma.OrbDiagnostics.') -or $_.FullName.StartsWith('FeedLikeGrandma.TamingOrbSystem/OrbServerReport') })
    Require ($diagnosticTypes.Count -ge 7) 'Expected integrated client/report types and nested server report.'
    $gameQueries = @('get_instance','IsServer','IsDedicated','GetWorld','GetWorldUID','GetInventory','GetAllItems','GetItemAt','GetZDOID','ToString','Equals','IsNone','Size','GetPos','GetArray','ShowMessage','GetString','GetInt','GetBool','GetFloat','GetVec3','GetPrefab','GetOwner','GetPosition','GetZDO','FindInstance','GetAllZDOIDsWithHash','GetStableHashCode')
    $packageQueries = @('.ctor','ReadString','ReadZDOID','ReadVector3','ReadQuaternion','ReadBool','ReadInt','ReadSingle','ReadLong','GetPos','Size','GetArray')
    $callbacks = 0
    $accesses = 0
    foreach ($type in $diagnosticTypes) {
        Require (@($type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' }).Count -eq 0) "Diagnostic must have no second plugin entry: $type"
        foreach ($method in $type.Methods) {
            if ($type.Name -eq 'PassiveTrace' -and $method.Name -in @('Prefix','BoolPostfix','VoidPostfix','Finalizer','OutgoingPrefix','OutgoingPostfix','OutgoingFinalizer')) {
                Require ($method.ReturnType.FullName -eq 'System.Void') "Observer replaces result/exception: $method"
                Require (@($method.Parameters | Where-Object { $_.ParameterType.IsByReference }).Count -eq 0) "Observer writes arguments: $method"
                $callbacks++
            }
            if (-not $method.HasBody) { continue }
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                $opcode = $instruction.OpCode.Code.ToString()
                Require ($opcode -ne 'Calli') "Unreviewable indirect call: $method"
                if ($operand -isnot [Mono.Cecil.MethodReference] -and $operand -isnot [Mono.Cecil.FieldReference]) { continue }
                $owner = $operand.DeclaringType.FullName
                if ($operand -is [Mono.Cecil.MethodReference]) {
                    Require (-not ($owner -eq 'BepInEx.Configuration.KeyboardShortcut' -or $owner.StartsWith('UnityEngine.Input'))) "Config-only diagnostics must not poll shortcuts/input: $operand"
                    Require (-not ($owner.StartsWith('System.Reflection.') -and $operand.Name -in @('Invoke','SetValue','SetValueDirect'))) "Reflective execution/mutation: $operand"
                    Require ($operand.Name -notin @('CanUseStoredOutcome','RecoverOrbRecord','RefreshReleaseRecoveryIndex','ResolveOrbItem','Open','Upsert','TryReserve','ClaimOwnership','SetOwner')) "Diagnostic may not repeat a recovery or state-changing operation: $operand"
                    if ($type.FullName.StartsWith('FeedLikeGrandma.TamingOrbSystem/OrbServerReport')) {
                        Require (-not ($owner.StartsWith('System.IO.File') -or $owner -eq 'System.IO.Directory')) "Server report may only use existing logger: $operand"
                        Require (-not $owner.StartsWith('HarmonyLib.')) "Typed server report should not patch its own operation: $operand"
                        Require ($owner -ne 'Character') "Character methods can change caches; inspect stored values instead: $operand"
                    }
                }
                if ($opcode -in @('Stfld','Stsfld') -and $owner.StartsWith('FeedLikeGrandma.')) {
                    Require ($owner.StartsWith('FeedLikeGrandma.OrbDiagnostics.') -or $owner.StartsWith('FeedLikeGrandma.TamingOrbSystem/OrbServerReport')) "Diagnostic writes non-diagnostic FLG field: $operand"
                }
                $scope = $operand.DeclaringType.Scope.Name
                if ($scope -notin @('assembly_valheim','assembly_utils','assembly_guiutils')) { continue }
                $definition = $operand.Resolve()
                Require ($null -ne $definition -and $definition.IsPublic) "Nonpublic/unresolved direct game API: $operand"
                Require ($opcode -notin @('Stfld','Stsfld')) "Game state write: $operand"
                if ($opcode -in @('Ldflda','Ldsflda')) {
                    Require ($instruction.Next -and $instruction.Next.OpCode.Code.ToString() -eq 'Ldfld') "Game field address escapes: $operand"
                }
                if ($operand -is [Mono.Cecil.MethodReference]) {
                    $allowed = if ($owner -eq 'ZPackage') { $packageQueries } else { $gameQueries }
                    Require ($operand.Name -in $allowed) "Game call needs read-only review: $operand"
                    if ($operand.Name -eq 'ShowMessage') { Require ($owner -eq 'MessageHud') 'Only local diagnostic HUD messages allowed.' }
                }
                $accesses++
            }
        }
    }
    Require ($callbacks -eq 7) "Expected seven passive callbacks, found $callbacks"
    Require (@($types | Where-Object FullName -eq 'FeedLikeGrandma.OrbServerDiagnostics.FlgContract').Count -eq 0) 'Standalone server reflection adapter must not ship in main plugin.'
    Require (@($types | Where-Object FullName -eq 'FeedLikeGrandma.OrbServerDiagnostics.PassiveObserver').Count -eq 0) 'Standalone server self-Harmony observer must not ship in main plugin.'
    $orb = $types | Where-Object FullName -eq 'FeedLikeGrandma.TamingOrbSystem'
    $handler = $orb.Methods | Where-Object Name -eq 'HandleRecordedReleaseRequest'
    $outcomes = @($handler.Body.Instructions | Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] -and $_.Operand.Name -eq 'CanUseStoredOutcome' })
    Require ($outcomes.Count -eq 2) 'Recorded release retains its two mutually exclusive outcome checks; diagnostic must not add a third.'
    $plugin = $types | Where-Object FullName -eq 'FeedLikeGrandma.FeedLikeGrandmaPlugin'
    $bind = $plugin.Methods | Where-Object Name -eq 'BindConfigEntries'
    foreach ($key in @('OrbDiagnostics')) {
        $name = @($bind.Body.Instructions | Where-Object { $_.OpCode.Code.ToString() -eq 'Ldstr' -and $_.Operand -eq $key })
        Require ($name.Count -eq 1 -and $name[0].Previous.Operand -eq 'Debug') "Config key must be in Debug: $key"
        if ($key -eq 'OrbDiagnostics') { Require ($name[0].Next.OpCode.Code.ToString() -eq 'Ldc_I4_0') 'OrbDiagnostics must default to Toggle.Off.' }
        $call = $name[0].Next
        while ($call -and -not ($call.Operand -is [Mono.Cecil.MethodReference] -and $call.Operand.Name -eq 'config')) { $call = $call.Next }
        Require ($null -ne $call -and $call.Previous.OpCode.Code.ToString() -eq 'Newobj' -and $call.Previous.Previous.Previous.OpCode.Code.ToString() -eq 'Ldc_I4_0') "Diagnostic config must remain non-synced: $key"
    }
    foreach ($key in @('OrbDiagnosticsDumpKey', 'OrbDiagnosticsTraceKey')) {
        Require (@($plugin.Fields | Where-Object Name -eq $key).Count -eq 0) "Removed diagnostic shortcut field remains: $key"
        Require (@($bind.Body.Instructions | Where-Object { $_.OpCode.Code.ToString() -eq 'Ldstr' -and $_.Operand -eq $key }).Count -eq 0) "Removed diagnostic shortcut is still bound: $key"
    }
    Write-Output "PASS: $accesses original public game accesses, $callbacks void/non-ref observers; diagnostic scopes contain no game writes, RPC sends, reflective execution, or repeated recovery decision."
    Write-Output 'Debug.OrbDiagnostics is the sole diagnostic control, defaults Off and binds locally without server synchronization; no diagnostic input polling.'
    Write-Output 'Scoped static inspection only. Actual Unity lifecycle, complete control flow, external callbacks, file access failures and multiplayer still require runtime validation.'
} finally { $assembly.Dispose(); $resolver.Dispose() }
