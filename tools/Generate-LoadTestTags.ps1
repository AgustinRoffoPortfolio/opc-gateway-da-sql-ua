<#
.SYNOPSIS
    Genera los tres artefactos de un escenario: aliases para MatrikonOPC (CSV
    y XML) y tags para el gateway. Salen consistentes por construccion.
.EXAMPLE
    .\Generate-LoadTestTags.ps1 -TagCount 500
#>
[CmdletBinding()]
param(
    [int]$TagCount = 8000,
    [int]$ScanRateMs = 1000,
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\scratch')
)

# Tabla de roles: NOMBRE|TIPO|EU|MULTIPLICADOR|OFFSET
# 25 Double + 10 Boolean + 4 Int32 + 1 String = 40 tags por equipo.
$roleTable = @(
    'PRESION_ENTRADA|Double|bar|1|0'
    'PRESION_SALIDA|Double|bar|1|0'
    'TEMPERATURA_ENTRADA|Double|C|1|0'
    'TEMPERATURA_SALIDA|Double|F|1.8|32'
    'CAUDAL_INSTANTANEO|Double|m3h|1|0'
    'CAUDAL_TOTALIZADO|Double|m3|0.001|0'
    'NIVEL_TANQUE|Double|pct|0.01|0'
    'VIBRACION_X|Double|mms|0.01|0'
    'VIBRACION_Y|Double|mms|0.01|0'
    'CORRIENTE_MOTOR|Double|A|0.1|0'
    'TENSION_MOTOR|Double|V|1|0'
    'POTENCIA_ACTIVA|Double|kW|0.01|0'
    'VELOCIDAD_MOTOR|Double|rpm|1|0'
    'TORQUE|Double|Nm|0.1|0'
    'DENSIDAD|Double|kgm3|1|0'
    'VISCOSIDAD|Double|cP|0.01|0'
    'PH|Double|pH|0.001|0'
    'CONDUCTIVIDAD|Double|uScm|1|0'
    'HUMEDAD|Double|pct|0.01|0'
    'APERTURA_VALVULA|Double|pct|0.01|0'
    'SETPOINT_PRESION|Double|bar|1|0'
    'SETPOINT_TEMPERATURA|Double|C|1|0'
    'HORAS_SERVICIO|Double|h|0.1|0'
    'EFICIENCIA|Double|pct|0.01|0'
    'CONSUMO_ESPECIFICO|Double|kWhm3|0.001|0'
    'ESTADO_BOMBA|Boolean||1|0'
    'ESTADO_VALVULA|Boolean||1|0'
    'ALARMA_ALTA|Boolean||1|0'
    'ALARMA_BAJA|Boolean||1|0'
    'MODO_AUTOMATICO|Boolean||1|0'
    'PERMISO_ARRANQUE|Boolean||1|0'
    'FALLA_GENERAL|Boolean||1|0'
    'CONFIRMACION_MARCHA|Boolean||1|0'
    'SELECTOR_LOCAL_REMOTO|Boolean||1|0'
    'EN_MANTENIMIENTO|Boolean||1|0'
    'CONTADOR_ARRANQUES|Int32||1|0'
    'CONTADOR_ALARMAS|Int32||1|0'
    'CODIGO_FALLA|Int32||1|0'
    'PASO_SECUENCIA|Int32||1|0'
    'RECETA_ACTIVA|String||1|0'
)

# Los cuatro ItemID nativos de la rama Random del simulador.
$daSourceByType = @{
    'Double'  = 'Random.Real8'
    'Boolean' = 'Random.Boolean'
    'Int32'   = 'Random.Int4'
    'String'  = 'Random.String'
}

# Molde de un alias en el XML del simulador, calcado de config/demo-10.opcsim.xml.
# type="1" es fijo: no depende del tipo de dato de abajo.
# No hay escapado de entidades XML porque los nombres de $roleTable y los
# ItemID nativos son solo [A-Z0-9_.]. Si algun dia entra un & o un <, hay
# que escapar antes de interpolar.
$xmlAliasFormat = '  <PSTAlias name="{0}" itemPath="{1}" type="1"><Scaling enabled="0" type="0"/><Events enabled="0" source="Alias" severity="1" trigger="0" timestamp="0"/></PSTAlias>'

$rolesPerDevice  = $roleTable.Count
$devicesPerPlant = 20
$deviceCount     = [math]::Ceiling($TagCount / $rolesPerDevice)

$uaLines  = [System.Collections.Generic.List[string]]::new()
$daLines  = [System.Collections.Generic.List[string]]::new()
$xmlLines = [System.Collections.Generic.List[string]]::new()
$stamp    = (Get-Date).ToString('yyyy-MM-dd HH:mm')

$uaLines.Add("# Generado por Generate-LoadTestTags.ps1 el $stamp - $TagCount tags")
$uaLines.Add("# Nombres inventados. Decimales con PUNTO (InvariantCulture).")
$uaLines.Add('TAG_NAME_OPC_UA;SOURCE;SOURCE_TAG;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED')
$daLines.Add("# Generado por Generate-LoadTestTags.ps1 el $stamp - $TagCount aliases")

# El XML no lleva cabecera de comentario: no sabemos si el parser de Matrikon
# digiere <!-- -->, y no vale el riesgo. La trazabilidad va en el nombre del
# archivo y en docs/operacion.md.
$xmlLines.Add('<Matrikon.OPC.Simulation><CSimRootDevLink name="" description="Sim Server Root"/><PSTAliasGroup>')

$emitted = 0
for ($device = 1; $device -le $deviceCount -and $emitted -lt $TagCount; $device++) {
    $plant     = [int][math]::Floor(($device - 1) / $devicesPerPlant) + 1
    $plantId   = 'PLANTA_{0:D2}' -f $plant
    $deviceId  = 'EQUIPO_{0:D3}' -f $device

    foreach ($role in $roleTable) {
        if ($emitted -ge $TagCount) { break }

        $name, $type, $eu, $mult, $offset = $role -split '\|'
        $daItem    = $daSourceByType[$type]
        $aliasName = "${plantId}_${deviceId}_${name}"

        # El alias vive en el grupo raiz: el ItemID resultante lleva punto inicial.
        # Por eso el XML guarda el nombre SIN punto y el CSV de tags lo agrega.
        $uaLines.Add("$plantId.$deviceId.$name;OPCDA;.$aliasName;$type;$mult;$offset;$eu;$ScanRateMs;0;Read;$name de $deviceId;True")
        $daLines.Add(",$aliasName,$daItem,0,0,0,0,0,,,,,,,0,Alias,0,1,,0,0")
        # Los parentesis extra son obligatorios: dentro de un llamado a metodo
        # PowerShell toma la coma como separador de argumentos del metodo, y el
        # -f se quedaria con un solo valor.
        $xmlLines.Add(($xmlAliasFormat -f $aliasName, $daItem))

        $emitted++
    }
}

$xmlLines.Add('</PSTAliasGroup></Matrikon.OPC.Simulation>')

# El directorio puede no existir en un clon recien hecho, y Resolve-Path
# falla si no esta. La API de .NET lo crea si falta, no hace nada si ya
# existe, y devuelve la ruta normalizada (sin el ".." del medio).
# Path.Combine deja pasar $OutputDir tal cual si es absoluta, y la ancla
# a la ubicacion actual si el usuario paso una relativa.
$resolvedDir = [System.IO.Directory]::CreateDirectory(
    [System.IO.Path]::Combine($PWD.ProviderPath, $OutputDir)).FullName
$uaPath  = Join-Path $resolvedDir "tags-$TagCount.csv"
$daPath  = Join-Path $resolvedDir "aliases-$TagCount.csv"
$xmlPath = Join-Path $resolvedDir "scenario-$TagCount.opcsim.xml"

# ASCII a proposito: el contenido no tiene acentos y evita el BOM,
# que Matrikon podria no digerir al importar. El molde tampoco declara
# <?xml encoding=...?>, asi que no lo agregamos.
[System.IO.File]::WriteAllLines($uaPath, $uaLines, [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllLines($daPath, $daLines, [System.Text.Encoding]::ASCII)
[System.IO.File]::WriteAllLines($xmlPath, $xmlLines, [System.Text.Encoding]::ASCII)

Write-Host "Tags generados : $emitted"
Write-Host "Equipos        : $($device - 1) de $deviceCount previstos"
Write-Host "Plantas        : $plantId (ultima)"
Write-Host "CSV tags UA    : $uaPath"
Write-Host "CSV aliases DA : $daPath"
Write-Host "XML escenario  : $xmlPath"