namespace Gateway.Sql;

/// Una fila de CURR_DATA tal como viene de la base, sin interpretar.
///
/// Por que existe un tipo intermedio y ReadAll no devuelve TagSample directo:
/// separa la mitad que necesita la base (ejecutar la consulta) de la mitad que
/// tiene toda la logica (tipos, NULL, calidad, hora local a UTC, cruce de
/// nombres). Asi el mapeo es una funcion pura y se testea sin Docker, igual
/// que SqlOptionsValidator. En el driver DA esta division no existe porque el
/// SDK ya entrega el dato desarmado; aca lo desarmamos nosotros.
///
/// Es un tipo nuestro y no del cliente SQL, asi que no rompe el principio 1:
/// ningun SqlDataReader ni SqlConnection cruza el borde de Gateway.Sql.
///
/// V y Q son nullable porque las columnas lo son en el CREATE real (P1). Que
/// se publica en ese caso lo decide V2-16, en el mapeo.
public sealed record SqlTagRow(string Tag, DateTime Ts, float? V, short? Q);