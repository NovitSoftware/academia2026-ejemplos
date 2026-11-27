# MesaYa · Costos, latencia y eficiencia (.NET)

Demo de la presentación **Módulo 4 · Unidad 4 · Costos, latencia, eficiencia y fallback**. Una trayectoria del agente de reservas ("mesa para 4 a las 22 en Palermo") muestra qué se cobra en cada paso, dónde se va el tiempo y cuánto se ahorra con la caché de prefijo, el recorte de una tool y la compactación del historial. Todo se puede ver con la API o con el equipo propio de la demo de Ejecución local.

> Idea para llevar: *medí, recortá, ordená para la caché*. En un agente manda la entrada, y se decide por resolución, no por precio por token.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma base que la demo de Ejecución local y edge.

## Simulado y real

**Las trayectorias, los tiempos y los costos son simulados** (`static/sim.js`):

- Los **tokens** salen del texto real de cada bloque, a unos 4 caracteres por token: el sistema, las definiciones de las 4 tools, los turnos anteriores, la consulta y los resultados de las tools (la disponibilidad trae 12 mesas con 25 campos cada una). En la pestaña **Request del paso** se ve todo lo que viaja.
- Los **costos** usan precios ilustrativos: USD 3 por millón de entrada, USD 15 de salida y el prefijo en caché al 10 %, como en las slides. El perfil Local cuesta lo mismo por token, sin descuento por caché.
- La **caché** compara el prompt de cada paso con el del paso anterior, bloque por bloque desde el principio. El proveedor cachea en bloques de 128 tokens, desde 1.024.
- Los **tiempos** salen de TTFT = base + tokens nuevos ÷ velocidad de lectura, generación = tokens de salida ÷ tokens por segundo, y tools con distribución log-normal. Hay cola larga: el 5 % de las búsquedas de disponibilidad tarda de 2 a 5 s más, y el proveedor encola el 4 % de los pedidos.

**Lo real es opcional:** la pestaña **Ollama real** del escenario C manda dos pedidos con la fecha arriba y dos con la fecha abajo a un modelo instalado (`ministral-3:3b`) y mide la lectura del prompt. En la RX 6700 XT, con unos 3.800 tokens de prefijo, la lectura baja de 5,7 s a 0,05 s.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| El perfil Local (opcional) | Correr la demo de **Ejecución local y edge** y apretar **Usar como perfil Local**. Si no, se usa un perfil de ejemplo de `appsettings.json` |
| La prueba real de la caché (opcional) | Ollama con `ministral-3:3b` (se cambia en `appsettings.json`, `Ollama:ModeloCache`) |

No usa claves de ningún proveedor.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5910](http://localhost:5910). `Ctrl+C` la detiene. Otro puerto: `PUERTO=5911 ./iniciar.sh`. El puerto 6000 no sirve: Chrome lo bloquea.

## La pantalla

**Izquierda.** Los escenarios, el perfil (API o Local), las conversaciones por día para proyectar, y los controles de cada escenario.

**Centro.** El escenario: las columnas de tokens por paso, la cascada de tiempos con el histograma de 200 pedidos, o qué parte de cada request estaba en la caché.

**Derecha.** Pestañas que cambian con el escenario:

| Escenario | Pestañas |
|---|---|
| A | **Request del paso**: cada mensaje con sus tokens. **Cuenta**: la fórmula del costo, la tabla por paso y la proyección |
| B | **Dónde se va el tiempo**: el reparto, la fórmula de la respuesta final y qué mejorar. **p50 y p95**: los percentiles de los 200 pedidos |
| C | **Prefijo**: el prompt con lo estable y lo variable. **Recorte**: la disponibilidad completa contra la recortada. **Ollama real**: la prueba de verdad |

## Escenarios para la clase

| | Escenario | Qué se ve (perfil API) |
|---|---|---|
| A | ¿Qué se cobra en una trayectoria? | 5 llamadas al modelo. La entrada pasa de 1.597 a 4.402 tokens y es el 88 % del costo. USD 0,0068 el primer request, USD 0,0636 la trayectoria, USD 19.088 por mes con 10.000 conversaciones por día. `buscar_disponibilidad` devuelve 2.439 tokens y se relee 4 veces: el 53 % de la entrada |
| B | ¿Dónde se va el tiempo? | p50 de 9,7 s y p95 de 14,1 s; el promedio (10,3 s) no describe a nadie. Con tools en paralelo, p50 de 8,5 s; con respuesta corta, 7,3 s. Con el perfil Local, 90 s, y 74 de esos segundos son lectura del prompt |
| C | Caché y recorte | Fecha arriba: 0 % en caché, USD 0,0636. Fecha abajo: 74 % en caché, USD 0,0263. Con caché tibia y el recorte: USD 0,0136. Compactar ahorra poco más y en el paso 4 solo conserva el prefijo estable. Con el perfil Local la plata no cambia, el tiempo sí: el TTFT baja de 73 s a 19 s con la fecha abajo, y a 4 s con el recorte |

## Guion sugerido (~25 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–8 | 4.1, 4.2 | **A**: las columnas; clic en el paso 1 y en el 5 (pestaña **Request del paso**); la tabla de costo por tool; la proyección por mes |
| 8–15 | 4.3 | **B**: un pedido; **Correr 200 pedidos**; p50, promedio y p95; prender las mejoras de a una; pasar al perfil Local |
| 15–23 | 4.4, 4.5 | **C**: fecha arriba y abajo; caché tibia; recortar (pestaña **Recorte**); compactar; la tabla "una mejora tras otra"; pestaña **Ollama real** |
| 23–25 | 4.6 – 4.8 | Batching, fallback y degradación se cuentan con las slides; el 429 con `Retry-After` y el fallback a un modelo chico se vieron en la Clase 7 |

## Cómo está hecho

```text
Program.cs           API: el perfil Local (de la otra demo o de appsettings), el estado de Ollama y la prueba real de la caché
static/sim.js        el modelo: perfiles, bloques del prompt, la trayectoria paso a paso, caché, costo por tool, tiempos y percentiles
static/app.js        interfaz: trayectoria, cascada de latencia y caché y recorte
static/              HTML y CSS sin build con la guía visual de Novit
appsettings.json     URL de Ollama, modelo para la prueba de caché y el perfil Local de ejemplo
```

## Decisiones y límites

- **A es la trayectoria sin optimizar.** Fecha arriba, sin caché, sin recorte y tools en secuencia, para que los números de partida no cambien. Las mejoras están en B y C.
- **El razonamiento no se reenvía.** Como en la mayoría de las APIs, al historial vuelven las tool calls y sus resultados, no el razonamiento.
- **Costo por resolución** no se repite: se midió en la Clase 7 con la capability matrix. El escenario A lo nombra.
- **En local, la caché ahorra tiempo, no plata.** El equipo cuesta lo mismo por token; lo que baja es cuánto tiene que leer antes de responder.
- **Modelos híbridos.** Con `qwen3.5` o `granite4`, Ollama reutiliza el prefijo solo en parte (guarda puntos de control del estado). Por eso la prueba real usa `ministral-3:3b`, un transformer clásico.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| El navegador no abre `localhost:6000` | Chrome bloquea ese puerto. La demo usa el 5910 |
| El perfil Local dice "appsettings.json" | La demo de Ejecución local no guardó un perfil: escenario C, pestaña **Perfil para Costos**, **Usar como perfil Local**. Recargá esta página |
| «Sin Ollama: solo simulado» | Ollama no responde: `ollama serve`. Solo afecta la pestaña **Ollama real** |
| La prueba real no muestra ahorro | El modelo es híbrido o el prefijo es corto. Usá `ministral-3:3b` o `gemma3:1b` |
