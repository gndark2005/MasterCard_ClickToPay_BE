# Descifrado de payloads Mastercard

Librería .NET 10 para recibir `encryptedPayload` y devolver `DecryptedPayloadDto`.
Incluye descifrado real de JWE y validación de su integridad mediante `jose-jwt`.
La obtención de la llave privada queda pendiente, detrás de una interfaz que implementará
la aplicación consumidora. No contiene credenciales ni llama a Mastercard.

## Contrato de entrada

La API entrega el valor de `encryptedPayload`: un JWE compacto de **cinco segmentos**
separados por puntos. No entrega el JWS completo de Checkout (tres segmentos),
ni el JSON completo de la respuesta ni un token de autenticación de la API.

```text
Respuesta Checkout (JWS)
  → verificar firma y confianza en el emisor [pendiente en la integración]
  → extraer encryptedPayload (JWE)
  → IPayloadDecryptionService.DecryptAsync
  → DecryptedPayloadDto
  → procesamiento interno de la API / integración con el adquirente
```

El descifrado y el tag de integridad del JWE no sustituyen la verificación del JWS
de Mastercard. Esta librería no implementa todavía esa verificación ni la llamada Checkout.
`assuranceData`, ECI y los resultados 3DS pertenecen a la respuesta externa y no se
inventan ni se incorporan al DTO descifrado.

## Componentes

| Componente | Responsabilidad |
| --- | --- |
| `IPayloadDecryptionService` | Contrato que consume la API. |
| `PayloadDecryptionService` | Valida formato/perfil, descifra y deserializa. |
| `IPayloadDecryptionKeyProvider` | Proporciona una instancia RSA privada propiedad del llamador. |
| `DecryptPayloadRequest` | Contiene `EncryptedPayload`. |
| `DecryptedPayloadDto` | Token, vencimiento, criptograma, datos adicionales, consumidor y direcciones. |
| `PayloadDecryptionException` | Error tipado sin incluir información sensible. |
| `AddMastercardPayloadDecryption<TKeyProvider>` | Registro de dependencias con ciclo de vida scoped. |

El proveedor entrega una instancia RSA nueva en cada llamada; el servicio la libera.
Puede resolver una llave desde un certificado, un almacén o un gestor de secretos.
No debe reutilizar una instancia compartida ni usar rutas/URLs recibidas en el token.
El proveedor actual se elige por configuración de la API, no por datos del cliente.
Para múltiples comercios, la API deberá resolver el proveedor desde contexto autenticado.
Si se elige un HSM que no permite exponer una instancia RSA compatible, habrá que
adaptar el descifrado para usar sus operaciones remotas; esa integración no está incluida.

## Llaves necesarias

Para esta operación se necesita la **clave privada RSA del par Payload Encryption**,
cuya clave pública se registró en Mastercard durante el onboarding. El perfil inicial
exige RSA de al menos 2048 bits.

| Material | Uso |
| --- | --- |
| Clave pública / certificado de Payload Encryption | Mastercard cifra para el integrador. |
| Clave privada correspondiente | Esta librería descifra. Permanece en el backend. |
| Contraseña de `.p12` / `.pfx`, si se adopta ese formato | El proveedor carga la clave privada del contenedor. |
| Claves de verificación de Mastercard | Verificación del JWS externo, fuera de este servicio. |

No confundir el par de Payload Encryption con las credenciales de firma/autenticación
de las llamadas API. Un certificado que contiene solo la clave pública no puede descifrar.
No se necesita un servicio HTTP adicional: primero se puede implementar el proveedor
en el mismo proceso de la API. Quedan por decidir el origen de la llave, su identificación,
la gestión de contraseñas y la rotación.

## Consumo desde la API

Agregar una referencia al proyecto `MC_ClickToPay.Services.csproj` y registrar:

```csharp
using MC_ClickToPay.Services.DependencyInjection;

// ApiPayloadKeyProvider es la implementación que debe aportar la API.
builder.Services.AddMastercardPayloadDecryption<ApiPayloadKeyProvider>();
```

En el servicio de aplicación de la API:

```csharp
using MC_ClickToPay.Services.Abstractions;
using MC_ClickToPay.Services.Models;

public sealed class CheckoutApplicationService(IPayloadDecryptionService decryption)
{
    public Task<DecryptedPayloadDto> GetPaymentDataAsync(
        string encryptedPayload, CancellationToken cancellationToken)
    {
        return decryption.DecryptAsync(
            new DecryptPayloadRequest { EncryptedPayload = encryptedPayload },
            cancellationToken);
    }
}
```

También se puede instanciar `new PayloadDecryptionService(keyProvider)` sin DI.
Los números de token, fechas, teléfonos e identificadores se mantienen como cadenas
para preservar ceros iniciales. Los datos opcionales ausentes quedan en `null`.

## Alcance del perfil inicial y errores

- Admite `alg = RSA-OAEP-256` y `enc = A128CBC-HS256`, tomados del ejemplo del tutorial.
  Confirmar el perfil del entorno real antes de ampliar la lista. El token no selecciona
  automáticamente cualquier algoritmo soportado por la dependencia.
- Rechaza compresión (`zip`), extensiones críticas (`crit`), encabezados duplicados,
  entradas de más de 128 KiB y JWS completos.
- El DTO inicial soporta el payload tokenizado DSRP del tutorial. Exige `paymentToken`,
  `dynamicDataValue` y tipo `CARD_APPLICATION_CRYPTOGRAM_SHORT_FORM`.
  No valida todavía todas las reglas de negocio del adquirente.
- `PayloadDecryptionException.Error` distingue `InvalidInput`, `UnsupportedHeader`,
  `DecryptionFailed` e `InvalidPayload`. La API debe traducirlos a su contrato de errores;
  conviene no exponer diferencias criptográficas al cliente.
- Los errores operativos del proveedor de llaves y la cancelación se propagan por separado.
- No registrar payloads, llaves, tokens de pago ni criptogramas. El DTO está pensado para
  procesamiento backend; no devolverlo automáticamente al navegador ni guardarlo en logs.

## Validación local

Desde la raíz del repositorio:

```powershell
dotnet restore src/MC_ClickToPay.Services.slnx --source https://api.nuget.org/v3/index.json
dotnet test src/MC_ClickToPay.Services.slnx --no-restore
```

Las pruebas generan llaves RSA temporales: cubren el mapeo, llave incorrecta,
manipulación de ciphertext/tag, algoritmo no permitido, entradas inválidas,
límite de tamaño, contenido descifrado inválido y cancelación. No constituyen una
prueba de interoperabilidad con Mastercard: faltan un payload real de sandbox y su llave.

## Referencias

- [Obtener el payload de Checkout](https://developer.mastercard.com/mastercard-checkout-solutions/tutorial/integrate_apis_scof/step7/)
- [Descifrar el payload](https://developer.mastercard.com/mastercard-checkout-solutions/tutorial/integrate_apis_scof/step8/)
- [Implementación JOSE para .NET](https://github.com/dvsekhvalnov/jose-jwt)
