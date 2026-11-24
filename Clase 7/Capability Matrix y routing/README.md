# MesaYa · Capability Matrix y Model Routing (.NET)

Demo de la presentación **Módulo 4 · Unidad 2 · Capability Matrix y Model Routing**. Dieciséis tareas de MesaYa corren en tres perfiles de modelo y el código corrige cada respuesta. Con eso se arma la capability matrix medida, se elige un modelo por descarte, se comparan cinco estrategias de routing y se enfrentan las reglas con un clasificador.

> Idea para llevar: *el modelo correcto es el más barato que resuelve la tarea*. La matriz se mide, no se lee en la ficha.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que la demo de Proveedores y modelos.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Perfiles locales | Ollama en `localhost:11434` con `qwen3.5:2b` (económico) y `qwen3.5:4b` (generalista) |
| Perfil grande | Una API key de NVIDIA para `nvidia/nemotron-3-ultra-550b-a55b`. Sin clave, corre `qwen3.5:9b` local |

Si falta algún modelo local, la app lo descarga al iniciar.

## La clave de NVIDIA

Igual que en la demo de Proveedores: primero la variable de entorno `NVIDIA_API_KEY`; si no está, `Proveedores:nvidia:ApiKey` de appsettings. La clave real va en `appsettings.Local.json` (copiá `appsettings.Local.ejemplo.json`), que está en el `.gitignore`.

Sin clave, el perfil **Grande** corre `qwen3.5:9b` local y la interfaz lo avisa. La demo funciona igual, pero la diferencia con el generalista es menor que con `nemotron-3-ultra`.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5800](http://localhost:5800). `Ctrl+C` la detiene.

## La pantalla

**Izquierda.** Los tres perfiles (modelo, proveedor y precio ilustrativo por millón de tokens), los parámetros (costo de derivar a una persona, y tarea, umbral de calidad y latencia máxima para el descarte) y los escenarios.

**Centro.** El tablero: cada tarea en cada perfil. ✓ o ✗ es la corrección real. "valida" o "no valida" es lo que ve el validador, que no conoce la respuesta. Un clic en una celda muestra la llamada.

**Derecha.** Cinco pestañas:

| Pestaña | Qué muestra | Slides |
|---|---|---|
| Matriz | Aciertos por tipo de tarea, si el JSON está garantizado, latencia p50 y p95, tokens por segundo y costo cada 1.000 pedidos | 03–05 |
| Descarte | Los cuatro filtros sobre lo medido: requisitos duros, umbral de calidad, latencia y costo. Cambia con los controles de la izquierda | 06 |
| Router | Las cinco estrategias: tareas resueltas, llamadas, escalamientos, costo y costo por resolución. El camino de cada tarea por los modelos | 11, 13, 14 |
| Reglas o clasificación | Ocho mensajes ruteados por palabras clave y por un clasificador chico, contra lo que correspondía | 10, 12 |
| Llamada | La tarea, la respuesta, lo esperado, el veredicto del validador y el request | 05 |

## Las tareas

| Tipo | Ejemplo | Qué prueba | Validador del router |
|---|---|---|---|
| Clasificar | "Lo del sábado ya no va, borrala." | Tarea simple | La intención es una de las cuatro |
| Extraer a JSON | "Somos seis, mañana 20.30 en Belgrano." | Structured output | Sucursal válida, fecha no pasada, hora HH:MM, personas en rango |
| Elegir la tool | "Cancelá la reserva R-2291, porfa." | Tool calling | La tool existe y tiene los argumentos obligatorios |
| Razonar | 10 personas cancelan con 48 h | Reasoning sobre una política | Seña y porcentaje posibles, y la cuenta cierra |

El validador revisa la forma y las cuentas, no la regla. Por eso algunos errores pasan sin escalar, y eso también es parte de la clase.

## Escenarios para la clase

| | Escenario | Qué se ve (medido con `nemotron-3-ultra` en NVIDIA como perfil grande) |
|---|---|---|
| A | Medir la matriz | Económico 9/16, generalista 14/16, grande 15/16. El económico falla extraer y razonar; el validador lo detecta en casi todos los casos. Sin clave, el grande (`qwen3.5:9b` local) acierta 14/16 |
| B | Elegir por descarte | Para extraer a JSON queda el generalista; para razonar, el grande |
| C | Router con cascada | Reglas + cascada resuelve 14/16 con menos de la mitad del gasto en modelos que siempre el grande (15/16). Si derivar cuesta USD 0,50, gana siempre el grande: un error es más caro que el modelo. Con USD 0,01 gana reglas + cascada. Si fallar no costara nada, ganaría el económico. Lo que pierde el router es una tarea (e3) que el económico contesta mal y el validador da por buena |
| D | Reglas o clasificación | Reglas 5/8, clasificador 6/8. Se equivocan en mensajes distintos: por eso se combinan |

## Guion sugerido (~25 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–7 | 03–05 | **A**: el tablero se llena; pestaña **Matriz** |
| 7–12 | 06 | **B**: extraer y razonar; mover el umbral y la latencia |
| 12–20 | 11, 13, 14 | **C**: las cinco estrategias, el camino de cada tarea y el costo de derivar |
| 20–25 | 10, 12 | **D**: reglas contra clasificador |

## Modo guion

Cada llamada que termina bien se guarda en `grabaciones/llm/`. Con **Modo guion** prendido se repite desde ahí sin llamar a los modelos. La carpeta ya trae grabados los cuatro escenarios con las dos variantes del perfil grande: `nemotron-3-ultra-550b-a55b` en NVIDIA (con clave) y `qwen3.5:9b` local (sin clave). Si cambiás un modelo, hay que correr el escenario A una vez en vivo.

## Lo que se guarda

En `datos/` (no va al repositorio): el resultado de cada tarea en cada perfil y la última corrida de cada escenario. El router reutiliza lo medido y solo llama a un modelo si falta algo. **Borrar los resultados** limpia todo.

## Cómo está hecho

```text
Program.cs              API: medir, rutear, clasificar y lo que se guarda en datos/
Evaluacion.cs           perfiles, armado del pedido por tipo, validador, corrección, matriz, estrategias y clasificador
Comun/Proveedores.cs    cliente único compatible con OpenAI (el mismo que la demo de Proveedores)
Comun/Local.cs          el Ollama local: modelos instalados y descargas
escenarios.json         escenarios A–D, la política de señas, las 16 tareas y los 8 mensajes del router
appsettings.json        proveedores y perfiles con su precio ilustrativo
static/                 interfaz (HTML, CSS y JS sin build) con la guía visual de Novit
```

## Decisiones y límites

- **Precios ilustrativos.** Ollama no cobra por token. Cada perfil tiene un precio de ejemplo por millón de tokens para que el costo se pueda comparar; se cambia en `appsettings.json`.
- **La corrección la hace código.** Cada tarea trae la respuesta esperada. El validador del router es otra función que no la conoce: decide si escalar.
- **El descarte se calcula en el navegador** sobre la matriz medida, así que los controles cambian el resultado sin volver a llamar a los modelos.
- **Costo por resolución** = (gasto en modelos + tareas no resueltas × costo de derivar) ÷ tareas resueltas.
- **Pocas tareas.** Dieciséis tareas alcanzan para ver diferencias en clase, no para elegir un modelo en producción.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama» o «Preparando…» | Ollama no responde en `localhost:11434` (`ollama serve`) o está descargando un modelo |
| El descarte no deja ningún perfil | Bajá el umbral o subí la latencia máxima. La primera llamada a `qwen3.5:9b` incluye la carga del modelo y sube su p95 |
| El grande da error con NVIDIA | Revisá la clave y que el modelo siga en el catálogo: `../probar-modelos-nvidia.sh` |
| «No hay una grabación de esta llamada» | En modo guion hay que repetir los mismos escenarios con los mismos modelos |
