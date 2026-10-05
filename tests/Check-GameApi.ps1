param(
    [Parameter(Mandatory = $true)][string]$CecilPath,
    [Parameter(Mandatory = $true)][string]$PluginDll,
    [Parameter(Mandatory = $true)][string]$ManagedPath,
    [Parameter(Mandatory = $true)][string]$BepInExCore
)
$ErrorActionPreference = 'Stop'
Add-Type -Path $CecilPath
$resolver = [Mono.Cecil.DefaultAssemblyResolver]::new()
$resolver.AddSearchDirectory($ManagedPath)
$resolver.AddSearchDirectory($BepInExCore)
$resolver.AddSearchDirectory((Split-Path -Parent ([IO.Path]::GetFullPath($PluginDll))))
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.AssemblyResolver = $resolver
$plugin = [Mono.Cecil.AssemblyDefinition]::ReadAssembly([IO.Path]::GetFullPath($PluginDll), $reader)
$gameScopes = @('assembly_valheim', 'assembly_utils', 'assembly_guiutils')
function Get-AllTypes($Types) {
    foreach ($type in $Types) { $type; Get-AllTypes $type.NestedTypes }
}
function Get-Methods($Type, $Name) {
    $found = @($Type.Methods | Where-Object Name -eq $Name)
    if ($found.Count -gt 0) { return $found }
    if ($Type.BaseType) { Get-Methods $Type.BaseType.Resolve() $Name }
}
function Get-Field($Type, $Name) {
    $found = $Type.Fields | Where-Object Name -eq $Name
    if ($found) { return $found }
    if ($Type.BaseType) { Get-Field $Type.BaseType.Resolve() $Name }
}
function Get-Signature($Parameters) {
    (@($Parameters | ForEach-Object { $_.ParameterType.FullName }) -join ',')
}
try {
    $references = 0
    $targets = 0
    $bindings = 0
    foreach ($type in (Get-AllTypes $plugin.MainModule.Types)) {
        foreach ($method in $type.Methods) {
            if (-not $method.HasBody) { continue }
            $lastString = ''
            foreach ($instruction in $method.Body.Instructions) {
                $operand = $instruction.Operand
                if ($instruction.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr) { $lastString = $operand }
                if ($operand -is [Mono.Cecil.MemberReference] -and $operand.DeclaringType.Scope.Name -in $gameScopes) {
                    $resolved = $operand.Resolve()
                    if (-not $resolved) { throw "Unresolved game member in $method : $operand" }
                    if ($resolved -is [Mono.Cecil.FieldDefinition] -and $resolved.IsLiteral -and $instruction.OpCode.Code -in @('Ldsfld', 'Ldsflda', 'Stsfld')) {
                        throw "Runtime instruction references constant field in $method : $operand"
                    }
                    if (($resolved -is [Mono.Cecil.FieldDefinition] -or $resolved -is [Mono.Cecil.MethodDefinition]) -and -not $resolved.IsPublic) {
                        throw "Direct non-public game access in $method : $operand"
                    }
                    $references++
                }
                if ($type.FullName -ne 'FeedLikeGrandma.GameAccess' -or $method.Name -ne '.cctor' -or
                    $operand -isnot [Mono.Cecil.GenericInstanceMethod]) { continue }
                if ($operand.Name -eq 'FieldRefAccess') {
                    $owner = $operand.GenericArguments[0].Resolve()
                    $field = Get-Field $owner $lastString
                    if (-not $field -or $field.IsStatic) { throw "Missing instance field $owner :: $lastString" }
                    $requested = $operand.GenericArguments[1].FullName
                    $actual = $field.FieldType.FullName
                    $compatible = $requested -eq $actual
                    if ($requested -in @('System.Collections.IDictionary', 'System.Collections.IList')) {
                        $compatible = $field.FieldType.Resolve().Interfaces.InterfaceType.FullName -contains $requested
                    }
                    if ($requested -eq 'System.Int32' -and $field.FieldType.Resolve().IsEnum) {
                        $underlying = $field.FieldType.Resolve().Fields | Where-Object Name -eq 'value__'
                        $compatible = $underlying.FieldType.FullName -eq 'System.Int32'
                    }
                    if (-not $compatible) { throw "Field binding type mismatch: $owner :: $lastString ($actual / $requested)" }
                    $bindings++
                } elseif ($operand.Name -eq 'MethodDelegate') {
                    $delegate = $operand.GenericArguments[0]
                    $arguments = @($delegate.GenericArguments)
                    $owner = $arguments[0].Resolve()
                    $returnType = 'System.Void'
                    $end = $arguments.Count
                    if ($delegate.Name.StartsWith('Func')) { $returnType = $arguments[-1].FullName; $end-- }
                    $signature = (@(for ($i = 1; $i -lt $end; $i++) { $arguments[$i].FullName }) -join ',')
                    $matches = @(Get-Methods $owner $lastString | Where-Object {
                        -not $_.IsStatic -and $_.ReturnType.FullName -eq $returnType -and (Get-Signature $_.Parameters) -eq $signature
                    })
                    if ($matches.Count -ne 1) { throw "Method binding mismatch: $owner :: $lastString ($signature)" }
                    $bindings++
                }
            }
        }
        $entries = @()
        foreach ($attribute in $type.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }) {
            $entries += [pscustomobject]@{ Attribute = $attribute; Hooks = @($type.Methods | Where-Object Name -in @('Prefix', 'Postfix', 'Finalizer')) }
        }
        foreach ($hook in $type.Methods) {
            foreach ($attribute in $hook.CustomAttributes | Where-Object { $_.AttributeType.FullName -eq 'HarmonyLib.HarmonyPatch' }) {
                $entries += [pscustomobject]@{ Attribute = $attribute; Hooks = @($hook) }
            }
        }
        foreach ($entry in $entries) {
            $attribute = $entry.Attribute
            $args = $attribute.ConstructorArguments
            if ($args.Count -lt 2) { continue } # Optional mod/dynamic targets have separate runtime checks.
            $owner = $args[0].Value.Resolve()
            $name = $args[1].Value
            $methods = @(Get-Methods $owner $name)
            if ($args.Count -ge 3) {
                $signature = (@($args[2].Value | ForEach-Object { $_.Value.FullName }) -join ',')
                $methods = @($methods | Where-Object { (Get-Signature $_.Parameters) -eq $signature })
            }
            if ($methods.Count -ne 1) { throw "Harmony target is missing or ambiguous: $type -> $owner :: $name" }
            $original = $methods[0]
            foreach ($hook in $entry.Hooks) {
                foreach ($parameter in $hook.Parameters) {
                    if ($parameter.Name -eq '__result' -and $parameter.ParameterType.FullName.TrimEnd('&') -ne $original.ReturnType.FullName) {
                        throw "Harmony result type mismatch: $type :: $hook"
                    }
                    if ($parameter.Name.StartsWith('__')) { continue }
                    $argument = $original.Parameters | Where-Object Name -eq $parameter.Name
                    if (-not $argument -or $parameter.ParameterType.FullName.TrimEnd('&') -ne $argument.ParameterType.FullName.TrimEnd('&')) {
                        throw "Harmony argument mismatch: $type :: $hook -> $parameter"
                    }
                }
            }
            $targets++
        }
    }
    # Hotbar's private nested element type cannot appear in a C# reference against the original DLL.
    $game = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $ManagedPath 'assembly_valheim.dll'), $reader)
    try {
        $element = $game.MainModule.GetType('HotkeyBar').NestedTypes | Where-Object Name -eq 'ElementData'
        if ((Get-Field $element 'm_icon').FieldType.FullName -ne 'UnityEngine.UI.Image' -or
            (Get-Field $element 'm_used').FieldType.FullName -ne 'System.Boolean') { throw 'Hotbar field mismatch' }
        foreach ($spec in @(
            'StatusEffect|GetTooltipString||System.String', 'SE_Stats|GetTooltipString||System.String',
            'FejdStartup|SetupGui||System.Void', 'FejdStartup|Start||System.Void',
            'ZNet|ListContainsId|SyncedList,System.String|System.Boolean', 'ZNet|GetPeer|ZRpc|ZNetPeer'
        )) {
            $parts = $spec.Split('|')
            $matches = @(Get-Methods ($game.MainModule.GetType($parts[0])) $parts[1] | Where-Object {
                (Get-Signature $_.Parameters) -eq $parts[2] -and $_.ReturnType.FullName -eq $parts[3]
            })
            if ($matches.Count -ne 1) { throw "Dynamic method mismatch: $spec" }
        }
        foreach ($spec in @(
            'ZNet|m_adminList|SyncedList', 'ZNet|m_connectionStatus|ZNet/ConnectionStatus',
            'ZNetPeer|m_socket|ISocket', 'ZRpc|m_socket|ISocket',
            'ZPlayFabSocket|m_remotePlayerId|System.String',
            'ZRoutedRpc|m_peers|System.Collections.Generic.List`1<ZNetPeer>',
            'ZRpc|m_functions|System.Collections.Generic.Dictionary`2<System.Int32,ZRpc/RpcMethodBase>'
        )) {
            $parts = $spec.Split('|')
            if ((Get-Field ($game.MainModule.GetType($parts[0])) $parts[1]).FieldType.FullName -ne $parts[2]) {
                throw "ServerSync reflection field mismatch: $spec"
            }
        }
    } finally { $game.Dispose() }
    Write-Output "PASS: $references direct game references, $targets explicit Harmony targets/arguments, $bindings private bindings, hotbar fields and 13 dynamic/reflection contracts."
    Write-Output 'Metadata/IL validation only; this does not execute Harmony detours, Unity, Mono, RPC transport, or optional mods.'
} finally { $plugin.Dispose(); $resolver.Dispose() }
