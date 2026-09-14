using Gateway.Core;

namespace Gateway.Tests;

public class TagValidatorTests
{
    private const string Header =
        "TAG_NAME_OPC_UA;SOURCE;SOURCE_TAG;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED";

    private const string HeaderV1 =
        "TAG_NAME_OPC_UA;TAG_NAME_OPC_DA;DATA_TYPE;MULTIPLICADOR;OFFSET;EU;SCAN_RATE_MS;DEADBAND;ACCESS_LEVEL;DESCRIPTION;ENABLED";

    // Escribe el contenido a un archivo temporal, corre carga y validacion,
    // y borra el archivo despues. La ruta que da Path.GetTempFileName() ya
    // es absoluta, asi que ConfigPathResolver la devuelve tal cual.
    private static TagLoadResult CargarDesdeContenido(string csvContent)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, csvContent);
            return TagValidator.LoadAndValidate(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CsvValido_CargaTodosLosTagsSinErrores()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Tag A;True",
            "PLANTA_01.TAG_B;OPCDA;Random.Real4;Double;2;0;bar;1000;0.1;Read;Tag B;True");

        var result = CargarDesdeContenido(csv);

        Assert.Equal(2, result.Tags.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ColumnaDeMenos_QuedaFueraDeServicioYElRestoCargaIgual()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Falta enabled",
            "PLANTA_01.TAG_B;OPCDA;Random.Real4;Double;2;0;bar;1000;0.1;Read;Tag B;True");

        var result = CargarDesdeContenido(csv);

        Assert.Single(result.Tags);
        Assert.Equal("PLANTA_01.TAG_B", result.Tags[0].OpcUaName);
        Assert.Single(result.Errors);
        Assert.Equal(2, result.Errors[0].LineNumber);
    }

    [Fact]
    public void TipoDeDatoInvalido_QuedaFueraDeServicio()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Entero;1;0;bar;1000;0.1;Read;Tipo invalido;True",
            "PLANTA_01.TAG_B;OPCDA;Random.Real4;Double;2;0;bar;1000;0.1;Read;Tag B;True");

        var result = CargarDesdeContenido(csv);

        Assert.Single(result.Tags);
        Assert.Equal("PLANTA_01.TAG_B", result.Tags[0].OpcUaName);
        Assert.Single(result.Errors);
        Assert.Equal("PLANTA_01.TAG_A", result.Errors[0].OpcUaName);
    }

    [Fact]
    public void NombreDuplicado_GanaLaPrimeraAparicion()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Primera aparicion;True",
            "PLANTA_01.TAG_A;OPCDA;Random.Real4;Double;2;0;bar;1000;0.1;Read;Segunda aparicion;True");

        var result = CargarDesdeContenido(csv);

        Assert.Single(result.Tags);
        Assert.Equal("Primera aparicion", result.Tags[0].Description);
        Assert.Single(result.Errors);
        Assert.Equal(3, result.Errors[0].LineNumber);
    }

    [Fact]
    public void CsvConCincoErroresDistintos_ArrancaIgualYReportaLosCinco()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_OK1;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Tag valido 1;True",
            "PLANTA_01.TAG_COLUMNA;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Falta enabled",
            "PLANTA_01.TAG_TIPO;OPCDA;Random.Real8;Entero;1;0;bar;1000;0.1;Read;Tipo invalido;True",
            "PLANTA_01.TAG_MULT;OPCDA;Random.Real8;Double;abc;0;bar;1000;0.1;Read;Multiplicador invalido;True",
            "PLANTA_01.TAG_ACCESO;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Write;Access level invalido;True",
            "PLANTA_01.TAG_OK2;OPCDA;Random.Real8;Double;1;0;bar;1000;0.1;Read;Tag valido 2;True",
            "PLANTA_01.TAG_OK2;OPCDA;Random.Real4;Double;1;0;bar;1000;0.1;Read;Tag valido 2 duplicado;True");

        var result = CargarDesdeContenido(csv);

        Assert.Equal(2, result.Tags.Count);
        Assert.Equal(5, result.Errors.Count);
    }

    // Regresion: el default de double.Parse acepta separador de miles, con lo
    // que "1,5" (un Excel en es-AR que piso el punto por coma) parseaba en
    // silencio como 15 y el tag quedaba escalado 10 veces mal. Tiene que ser
    // un error de carga, no un valor distinto.
    [Fact]
    public void MultiplicadorConComaDecimal_QuedaFueraDeServicio()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Double;1,5;0;bar;1000;0.1;Read;Coma decimal;True",
            "PLANTA_01.TAG_B;OPCDA;Random.Real4;Double;2;0;bar;1000;0.1;Read;Tag B;True");

        var result = CargarDesdeContenido(csv);

        Assert.Single(result.Tags);
        Assert.Equal("PLANTA_01.TAG_B", result.Tags[0].OpcUaName);
        Assert.Single(result.Errors);
        Assert.Equal("PLANTA_01.TAG_A", result.Errors[0].OpcUaName);
    }

    [Fact]
    public void DecimalesConPunto_ParseanConCulturaInvariante()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;OPCDA;Random.Real8;Double;1.5;-14.7;bar;1000;0.25;Read;Decimales validos;True");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Errors);
        Assert.Equal(1.5, result.Tags[0].Multiplier);
        Assert.Equal(-14.7, result.Tags[0].Offset);
        Assert.Equal(0.25, result.Tags[0].Deadband);
    }

    // A partir de aca, tests de V2-5: SOURCE obligatorio y lectura por
    // cabecera en vez de por posicion.

    [Fact]
    public void SourceAusente_EsErrorDeCarga()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;;Random.Real8;Double;1;0;bar;1000;0.1;Read;Sin source;True");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Tags);
        Assert.Single(result.Errors);
        Assert.Contains("SOURCE", result.Errors[0].Message);
    }

    [Fact]
    public void SourceDesconocido_EsErrorDeCarga()
    {
        var csv = string.Join('\n', Header,
            "PLANTA_01.TAG_A;MODBUS;Random.Real8;Double;1;0;bar;1000;0.1;Read;Source invalido;True");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Tags);
        Assert.Single(result.Errors);
        Assert.Contains("SOURCE", result.Errors[0].Message);
        Assert.Contains("OPCDA", result.Errors[0].Message);
    }

    [Theory]
    [InlineData("OPCDA", TagSource.OpcDa)]
    [InlineData("OPC_DA", TagSource.OpcDa)]
    [InlineData("opcda", TagSource.OpcDa)]
    [InlineData(" SQL ", TagSource.Sql)]
    public void SourceValoresAceptados_CarganConElEnumCorrecto(string source, TagSource esperado)
    {
        var csv = string.Join('\n', Header,
            $"PLANTA_01.TAG_A;{source};Random.Real8;Double;1;0;bar;1000;0.1;Read;Tag A;True");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Errors);
        Assert.Single(result.Tags);
        Assert.Equal(esperado, result.Tags[0].Source);
    }

    [Fact]
    public void CabeceraConColumnasEnOtroOrden_CargaBien()
    {
        const string headerReordenado =
            "SOURCE_TAG;SOURCE;ENABLED;TAG_NAME_OPC_UA;DESCRIPTION;ACCESS_LEVEL;DEADBAND;SCAN_RATE_MS;EU;OFFSET;MULTIPLICADOR;DATA_TYPE";
        var csv = string.Join('\n', headerReordenado,
            "Random.Real8;OPCDA;True;PLANTA_01.TAG_A;Tag A;Read;0.1;1000;bar;0;1;Double");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Errors);
        Assert.Single(result.Tags);
        Assert.Equal("PLANTA_01.TAG_A", result.Tags[0].OpcUaName);
        Assert.Equal(TagSource.OpcDa, result.Tags[0].Source);
        Assert.Equal("Random.Real8", result.Tags[0].SourceTag);
    }

    [Fact]
    public void CabeceraDeLaV1_DaErrorQueNombraTagNameOpcDa()
    {
        var csv = string.Join('\n', HeaderV1,
            "PLANTA_01.TAG_A;Random.Real8;Double;1;0;bar;1000;0.1;Read;Tag A;True");

        var result = CargarDesdeContenido(csv);

        Assert.Empty(result.Tags);
        Assert.Single(result.Errors);
        Assert.Contains("TAG_NAME_OPC_DA", result.Errors[0].Message);
        Assert.Contains("SOURCE_TAG", result.Errors[0].Message);
    }
}
