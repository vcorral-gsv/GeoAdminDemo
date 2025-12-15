# Geo Admin Demo

API .NET para importar y consultar áreas administrativas por país, con geometría (SQL Server geography), resolución de puntos y utilidades GeoJSON/WKT.

## Características
- Importación desde servicio ArcGIS (GlobalAdminBoundaries) por niveles (0..N)
- Resolución de punto ? jerarquía administrativa (iterativo y CTE)
- Endpoints para listar, detalle, geometría WKT y GeoJSON con simplificación
- Middleware de errores con ProblemDetails
- Swagger siempre activo y XML comments

## Requisitos
- SQL Server con soporte `geography`
- Cadena de conexión `DefaultConnection` en `appsettings.json`
- Config ArcGIS (opcional):
  - `ArcGIS:TokenUrl` y `ArcGIS:AuthToken` o usa header `X-ArcGIS-Auth` / query `?arcgisAuth=`
  - `ArcGIS:GeocodeBaseUrl` (default: World GeocodeServer)

## Ejecución local
1. Configura `appsettings.json`:
```
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=GeoAdminDemo;Trusted_Connection=True;TrustServerCertificate=True"
  },
  "ArcGIS": {
    "TokenUrl": "https://www.arcgis.com/sharing/rest/generateToken",
    "AuthToken": "Bearer <tu_token>"
  }
}
```
2. `dotnet run` y abre `/swagger`.

## Endpoints principales
- `POST /api/geo/import/esri?hardReset=false&iso3=ESP&maxLevel=4` ? Importa niveles por país
- `GET /api/geo/admin-areas?countryIso3=ESP&level=2&skip=0&take=50&q=Sev` ? Listado paginado
- `GET /api/geo/admin-areas/{id}` ? Detalle
- `GET /api/geo/admin-areas/{id}/geometry` ? WKT
- `GET /api/geo/admin-areas/{id}/geometry-geojson?zoom=10` ? GeoJSON (simplificado)
- `GET /api/geo/resolve-point?lat=40.4&lon=-3.7&iso3=ESP` ? Path administrativo
- `GET /api/geo/resolve-point-cte?lat=...` ? Variante CTE
- `GET /api/geo/admin-summary/{iso3}` ? Conteo por niveles

## Colección Postman
En `Postman/GeoAdminDemo.postman_collection.json` se incluye una colección con ejemplos y variables:
- `baseUrl` (ej: http://localhost:5000)
- `iso3` (ej: ESP)
- `arcgisAuth` (si necesitas token)

## Despliegue en Azure App Service
1. Publica el proyecto con `dotnet publish -c Release`.
2. Crea App Service (Windows/Linux) y un Azure SQL.
3. Configura settings:
   - `ConnectionStrings:DefaultConnection`
   - `ArcGIS:TokenUrl`, `ArcGIS:AuthToken` (o usa `X-ArcGIS-Auth`)
4. Sube el artefacto o usa GitHub Actions.
5. Asegura `WEBSITE_HTTPLOGGING_ENABLED` si quieres logs.

## Notas de diseño
- `GeoResolveService` centraliza lógica espacial y CTE.
- `EsriAdminImportService` encapsula importación, robustez y CSV opcional.
- `ExceptionHandlingMiddleware` devuelve ProblemDetails coherentes.
- Entidades y DTO documentados para Swagger.

## Licencia
MIT.
