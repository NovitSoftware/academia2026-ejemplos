# MesaYa · Ejecución local y edge (.NET)

Demo de la presentación **Módulo 4 · Unidad 3 · Ejecución local y edge**. Un banco de cuantización muestra cuánta memoria ocupa cada modelo, si entra en la VRAM, a qué velocidad lee y genera y cuántas tareas resuelve. La tablet del mozo sigue funcionando sin internet. La cuenta del punto de equilibrio dice cuándo se paga el equipo propio.

> Idea para llevar: *local para lo cercano, API para lo grande*. Más grande en Q4 antes que más chico en Q8, todo en la VRAM, y hacer la cuenta antes de comprar.

Está hecha en C# con .NET 10 (ASP.NET Core minimal API) y una interfaz web sin frameworks, con la misma guía visual que las demos de la Clase 7.

## Simulado y real

**El banco, la tablet y la cuenta son simulados**, con las mismas cuentas que las slides y ruido estadístico para que se vean como mediciones. No hace falta descargar modelos:

| Qué | Cómo se calcula (`static/sim.js`) |
|---|---|
| Memoria | parámetros × bits por peso ÷ 8 + contexto (KV cache, ~1 GB cada 8k en un 8B) + buffers |
| Si entra en la VRAM | lo que no entra va a la RAM (offloading) y se marca rayado |
| Generación (tokens/s) | limitada por el ancho de banda: cada token relee todos los pesos. Lo que está en la RAM se lee mucho más lento |
| Lectura (tokens/s) | limitada por el cómputo de la GPU |
| Aciertos | calidad base por tamaño × factor de la cuantización, sobre 60 tareas. No depende del equipo |
| Tablet | un modelo de 3B a 14 tokens/s que responde con la carta guardada; la nube responde como `nemotron-3.5-lightning` |

**Lo real es opcional** y usa modelos que ya están instalados:

- **Medir en este equipo** (escenario A): corre un pedido en un modelo de Ollama y muestra lo mismo que `ollama run --verbose` y `ollama ps`, junto a la estimación.
- **Modelo real** (escenario B): la tablet responde con `qwen3.5:2b` en este equipo, con la carta en el contexto.

## Qué necesitás

| Para qué | Requisito |
|---|---|
| Correr la demo | SDK de .NET 10 |
| Medir y la tablet real (opcional) | Ollama en `localhost:11434` con algún modelo instalado; para la tablet, `qwen3.5:2b` (se cambia en `appsettings.json`, `Ollama:ModeloMozo`) |

No usa claves de ningún proveedor.

## Iniciar

Desde esta carpeta:

```bash
./iniciar.sh
```

Abrí [http://localhost:5900](http://localhost:5900). `Ctrl+C` la detiene. Otro puerto: `PUERTO=5901 ./iniciar.sh`. Otro Ollama: `OLLAMA_URL=http://otra-maquina:11434 ./iniciar.sh`.

## La pantalla

**Izquierda.** Los escenarios y sus controles: el equipo, el contexto, la conexión de la sucursal o los costos.

**Centro.** El escenario: el banco con el gráfico de calidad contra memoria, la tablet del mozo o el gráfico de costo por volumen.

**Derecha.** Pestañas que cambian con el escenario:

| Escenario | Pestañas |
|---|---|
| A | **Modelo**: la cuenta de memoria, `ollama run --verbose` y `ollama ps` simulados y cómo se siente cada velocidad. **Medir en este equipo**: la medición real |
| B | **Red**: cada request con su destino y qué datos viajaron. **Dispositivo**: memoria, batería, temperatura y la regla del router |
| C | **Cuenta**: costo por equipo, capacidad y punto de equilibrio. **Perfil para Costos**: lo que toma la otra demo |

## Escenarios para la clase

| | Escenario | Qué se ve (en "Este equipo · RX 6700 XT") |
|---|---|---|
| A | Banco de cuantización | El 8B pasa de 17,5 GB en F16 a 5 GB en Q3. El 14B en Q4 (10,5 GB, 45/60) le gana al 8B en Q8 (10 GB, 42/60). El F16 y el 32B no entran: 62 % y 42 % en GPU y menos de 4 tokens/s. Con 32k de contexto, el 14B deja de entrar y cae de 23 a 5 tokens/s |
| B | La tablet sin internet | Alérgenos, platos y pedidos se responden en la tablet. El análisis del día va a la nube. Al cortar internet, la tablet sigue tomando pedidos y lo de la nube queda en cola. Al volver la conexión se sincroniza el turno. A la nube nunca viajan datos de mesas ni de clientes |
| C | ¿Cuándo se paga el equipo? | Equipo de USD 2.400 en 24 meses, energía y mantenimiento: USD 150 por mes. Contra la API a USD 2 por millón, el equilibrio está en 75 M tokens por mes. Con el 8B en Q4 y 4 pedidos en paralelo, un equipo rinde ~260 M tokens por mes; más volumen pide otro equipo (la curva sube en escalones) |

## Guion sugerido (~25 min)

| Min | Slides | En la demo |
|---|---|---|
| 0–3 | 3.1 | Pestaña **Medir en este equipo**: un modelo instalado, real, con `--verbose` y `ps` |
| 3–12 | 3.2, 3.3, 3.4 | **A**: correr el banco; la fila del 8B en Q8 contra el 14B en Q4; subir el contexto a 32k; cambiar a la notebook de 8 GB y a "Solo CPU" |
| 12–20 | 3.5, 3.6, práctica | **B**: las preguntas de la carta y un pedido; cortar internet; pedir el análisis del día; volver a conectar. Pestaña **Red** |
| 20–25 | 3.7 | **C**: mover el volumen; pasar a 1 pedido en paralelo; elegir el 32B y ver que lo local no llega. **Usar como perfil Local** para la demo de Costos |

## El hilo con la demo de Costos

La pestaña **Perfil para Costos** guarda `datos/perfil-local.json` con el modelo, sus tokens por segundo y el costo por millón de tokens al volumen elegido. La demo de **Costos, latencia y fallback** lo lee al iniciar y lo usa como perfil **Local** junto al de la API.

## Cómo está hecho

```text
Program.cs           API: estado de Ollama, medir un modelo instalado, la tablet real y el perfil para la otra demo
static/sim.js        el modelo de simulación: equipos, cuantizaciones, memoria, velocidades, calidad, la carta y el router
static/app.js        interfaz: el banco, la tablet y el punto de equilibrio
static/              HTML y CSS sin build con la guía visual de Novit
appsettings.json     URL de Ollama y modelo de la tablet real
```

## Decisiones y límites

- **Simulado a propósito.** Descargar y medir cada cuantización lleva demasiado tiempo para una clase. Las cuentas son las de las slides; los valores son ilustrativos y del orden de lo que se mide.
- **La estimación no es la medición.** "Medir en este equipo" pone las dos juntas. Con `qwen3.5:4b` en la RX 6700 XT, la memoria coincide y la generación real es cerca de la mitad de la estimada: la arquitectura, el driver y el backend mueven los números.
- **El router de la tablet es una regla.** Palabras como "análisis", "propuesta" o "evento" van a la nube; el resto se resuelve en el dispositivo.
- **La capacidad mezcla lectura y generación.** 80 % de tokens de entrada y 20 % de salida, con un factor por pedidos en paralelo.

## Problemas frecuentes

| Síntoma | Causa y solución |
|---|---|
| «Sin Ollama: solo simulado» | Ollama no responde: `ollama serve`. La demo funciona igual |
| El modelo real de la tablet está deshabilitado | Falta `qwen3.5:2b` en Ollama, o cambialo en `appsettings.json` |
| La primera medición tarda | Incluye la carga del modelo desde el disco; la segunda es la que vale |
| La medición dice "CPU/GPU" | El modelo no entró entero en la VRAM, o Ollama no usa la GPU: revisá `ollama ps` |
