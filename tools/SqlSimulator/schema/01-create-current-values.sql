-- Esquema de la tabla de origen CURRENT_VALUES, replicado para desarrollo local.
-- Fiel al CREATE real que paso mi padre (P1 en docs/v2/requisitos.md):
-- mismos tipos, misma nulabilidad, misma clave primaria.
--
-- El gateway NUNCA crea esta tabla: en produccion ya existe y la escribe
-- otra aplicacion. Este script existe solo para el simulador.
--
-- Se aplica con sqlcmd. Es idempotente: se puede correr varias veces.

IF DB_ID('PLANT_DB') IS NULL
BEGIN
    CREATE DATABASE [PLANT_DB];
END
GO

USE [PLANT_DB];
GO

IF OBJECT_ID('dbo.CURRENT_VALUES', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CURRENT_VALUES
    (
        -- Nombre del tag del lado SQL. Collation explicito y no heredado de la
        -- instancia: CI = case insensitive, para que el cruce de nombres contra
        -- el CSV se comporte igual en cualquier servidor (decision V2-17).
        [TAG] varchar(50) COLLATE SQL_Latin1_General_CP1_CI_AS NOT NULL,

        -- Hora local de la aplicacion de origen (P4). Resolucion de unos 3 ms.
        [TS]  datetime NOT NULL,

        -- Valor. Float de 4 bytes, unos 7 digitos significativos (P1).
        [V]   real NULL,

        -- Calidad en codigo OPC DA. Entero con signo (P1, P5).
        [Q]   smallint NULL,

        -- Una sola fila por tag, que se pisa con UPDATE (P3).
        CONSTRAINT PK_CURRENT_VALUES PRIMARY KEY CLUSTERED ([TAG])
    );
END
GO