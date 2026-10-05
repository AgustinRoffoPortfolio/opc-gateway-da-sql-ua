using Gateway.Sql;

namespace Gateway.Tests;

public class SqlIdentifierTests
{
    [Fact]
    public void IdentificadoresLegitimos_SeAceptan()
    {
        Assert.True(SqlIdentifier.IsValid("TAG"));
        Assert.True(SqlIdentifier.IsValid("dbo"));
        Assert.True(SqlIdentifier.IsValid("CURRENT_VALUES"));
        Assert.True(SqlIdentifier.IsValid("PLANT_DB"));
        Assert.True(SqlIdentifier.IsValid("_interna"));
        Assert.True(SqlIdentifier.IsValid("T1"));
    }

    [Fact]
    public void VacioONulo_SeRechaza()
    {
        Assert.False(SqlIdentifier.IsValid(null));
        Assert.False(SqlIdentifier.IsValid(""));
    }

    [Fact]
    public void ArrancaConDigito_SeRechaza()
    {
        Assert.False(SqlIdentifier.IsValid("1TAG"));
        Assert.False(SqlIdentifier.IsValid("2026_DATA"));
    }

    // El caso que justifica la lista blanca: un valor de appsettings.json
    // termina concatenado en el texto del SQL porque un nombre de tabla no
    // puede viajar como parametro (V2-9). Ninguno de estos tiene que pasar.
    [Fact]
    public void IntentosDeInyeccion_SeRechazan()
    {
        Assert.False(SqlIdentifier.IsValid("CURRENT_VALUES; DROP TABLE USUARIOS"));
        Assert.False(SqlIdentifier.IsValid("CURRENT_VALUES--"));
        Assert.False(SqlIdentifier.IsValid("dbo]"));
        Assert.False(SqlIdentifier.IsValid("a'b"));
        Assert.False(SqlIdentifier.IsValid("dbo.CURRENT_VALUES"));
        Assert.False(SqlIdentifier.IsValid("CURRENT VALUES"));
        Assert.False(SqlIdentifier.IsValid("CURRENT-VALUES"));
    }

    // SQL Server aceptaria acentos entre corchetes; la lista blanca es a
    // proposito mas angosta que el motor. Se amplia si aparece un nombre real
    // que la necesite, nunca aflojandola en el momento.
    [Fact]
    public void NoAscii_SeRechaza()
    {
        Assert.False(SqlIdentifier.IsValid("PRESION_MAXIMA_Ñ"));
        Assert.False(SqlIdentifier.IsValid("TEMPERATURA_°C"));
    }

    [Fact]
    public void LimiteDe128Caracteres_AceptaJustoYRechazaUnoMas()
    {
        Assert.True(SqlIdentifier.IsValid(new string('A', 128)));
        Assert.False(SqlIdentifier.IsValid(new string('A', 129)));
    }

    [Fact]
    public void QualifyTable_ArmaElNombreDeLaConsultaDeR3()
    {
        var nombre = SqlIdentifier.QualifyTable("PLANT_DB", "dbo", "CURRENT_VALUES");

        Assert.Equal("[PLANT_DB].[dbo].[CURRENT_VALUES]", nombre);
    }

    // Quote tiene que ser correcta sola, sin depender de que el llamador haya
    // pasado antes por IsValid. Con la validacion puesta este caso no puede
    // llegar, y justamente por eso el test es el unico lugar donde se prueba.
    [Fact]
    public void Quote_DuplicaElCorcheteDeCierre()
    {
        Assert.Equal("[a]]b]", SqlIdentifier.Quote("a]b"));
        Assert.Equal("[CURRENT_VALUES]", SqlIdentifier.Quote("CURRENT_VALUES"));
    }
}