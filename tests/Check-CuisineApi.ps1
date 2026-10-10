param(
    [Parameter(Mandatory = $true)][string]$CecilPath,
    [Parameter(Mandatory = $true)][string]$CuisineDll
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilPath
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($CuisineDll))
try {
    $plugin = $assembly.MainModule.GetType('ValheimCuisine.ValheimCuisinePlugin')
    if (-not $plugin) { throw 'Missing Cuisine plugin type' }
    $metadata = $plugin.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin' }
    if ($metadata.ConstructorArguments[0].Value -ne 'XutzBR.ValheimCuisine') { throw 'Unexpected Cuisine plugin identity' }
    foreach ($spec in @(
        'CooldownDuration|System.Single', 'MilkAmount|System.Int32', 'RequireBreeding|System.Boolean',
        'MilkingKey|BepInEx.Configuration.KeyboardShortcut'
    )) {
        $parts = $spec.Split('|')
        $field = $plugin.Fields | Where-Object Name -eq $parts[0]
        if (-not $field.IsStatic -or $field.FieldType.FullName -ne ('BepInEx.Configuration.ConfigEntry`1<' + $parts[1] + '>')) {
            throw "Cuisine config binding mismatch: $spec"
        }
    }
    foreach ($spec in @(
        'LoxMilkingUpdatePatch|Postfix|Player',
        'LoxMilkingUpdatePatch|MilkLox|Player,Character,Tameable',
        'LoxMilkingHoverTextPatch|Postfix|Tameable,System.String&'
    )) {
        $parts = $spec.Split('|')
        $type = $plugin.NestedTypes | Where-Object Name -eq $parts[0]
        $methods = @($type.Methods | Where-Object {
            $_.Name -eq $parts[1] -and $_.IsStatic -and -not $_.HasGenericParameters -and $_.ReturnType.FullName -eq 'System.Void' -and
            (($_.Parameters.ParameterType.FullName) -join ',') -eq $parts[2]
        })
        if ($methods.Count -ne 1) { throw "Cuisine method binding mismatch: $spec" }
    }
    foreach ($spec in @('LoxMilkingUpdatePatch|Player|Update', 'LoxMilkingHoverTextPatch|Tameable|GetHoverText')) {
        $parts = $spec.Split('|')
        $type = $plugin.NestedTypes | Where-Object Name -eq $parts[0]
        $patch = $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }
        if ($patch.ConstructorArguments[0].Value.FullName -ne $parts[1] -or
            $patch.ConstructorArguments[1].Value -ne $parts[2]) { throw "Cuisine Harmony target mismatch: $spec" }
    }
    Write-Output 'PASS: Cuisine plugin identity, four config fields, three hook signatures and two Harmony targets.'
    Write-Output ('Detected Cuisine version (informational): ' + $metadata.ConstructorArguments[2].Value)
    Write-Output ('Cuisine SHA256: ' + (Get-FileHash -LiteralPath $CuisineDll -Algorithm SHA256).Hash)
    Write-Output 'Original DLL metadata only; no plugin code, Harmony detours, Unity or network runtime was executed.'
} finally { $assembly.Dispose() }
