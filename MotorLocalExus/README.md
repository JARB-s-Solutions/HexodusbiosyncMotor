# HEXODUS BioSync 2.2.1 - Windows 11 x64

Este directorio contiene los proyectos del motor local de HEXODUS. La guía de arquitectura, compilación, configuración e instalación está en [`../README.md`](../README.md).

## Estructura

- `Engine`: motor SSE V2 como servicio Windows, configuracion DPAPI, diagnostico local y monitor del detector.
- `Reader`: auxiliar interactivo que abre el SDK DigitalPersona en la sesion del usuario y se reconecta automaticamente.
- `Desktop`: ventana de estado y configuracion. Solo la instalacion, configuracion y desinstalacion utilizan elevacion.
- `Shared`: configuracion protegida, permisos de archivos y contratos locales sin biometria ni secretos.
- `Installer`: asistente Inno Setup, comprobacion opcional del lector, servicio, acceso directo y arranque automatico.
- `Tests`: comprobaciones aisladas de protocolo, SSE, cola y configuracion. No llaman a Supabase.

El motor conserva el protocolo v1, las rutas SSE y el puerto actual. La comparacion biometrica, la cache, los permisos HTTP, la firma y la cola mantienen sus algoritmos. El detector requiere una pantalla SSE autorizada antes de capturar asistencia y vuelve a comprobarla al terminar la captura. El enrolamiento mantiene su autorizacion propia.

## Seguridad y distribucion

No se incluye una clave de dispositivo ni credenciales de Supabase en el instalador. Los ajustes se guardan cifrados para la maquina mediante DPAPI y con ACLs de administradores, SYSTEM y LocalService. La ventana recibe exclusivamente un resumen por named pipe; no puede generar capacidades ni registrar asistencias. El pipe permite usuarios interactivos locales y deniega acceso de red.

El archivo `.exusconfig` preparado fuera del instalador solo permite importar ajustes en la computadora que lo genero. En otra PC se configuran los valores autorizados existentes en HEXODUS. No hay migracion de esquema.

El servicio usa LocalService. El auxiliar del lector usa la sesion interactiva porque el SDK USB no funciona desde Session 0. Ningun componente solicita elevacion cada vez que se enciende Windows.

La distribucion publica puede requerir la firma comercial de HEXODUS para evitar advertencias de SmartScreen. No se agregan certificados autofirmados, exclusiones de antivirus ni cambios globales de seguridad.

## Construccion

Ejecutar desde la raiz:

`powershell -NoProfile -File .\tools\compilar-motor-exus.ps1 -DPUruNetDll 'C:\ruta\del\SDK\DPUruNet.dll'`

Para una distribución protegida por Smart App Control, usa la firma comercial
de Microsoft Artifact Signing. El flujo firma los ejecutables antes de
empaquetarlos y firma también el instalador. Las instrucciones están en
[`../README.md`](../README.md) y los marcadores de ejemplo en
[`../tools/configurar-firma-microsoft.sample.json`](../tools/configurar-firma-microsoft.sample.json).

Las pruebas automatizadas usan archivos temporales aislados. No insertan socios ni asistencias en la base de datos.
