import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");
const comun = { bundle: true, sourcemap: true, logLevel: "info", minify: !watch };

const contextos = await Promise.all([
  // Código de la extensión: corre en Node dentro de VS Code.
  esbuild.context({ ...comun, entryPoints: ["src/extension.ts"], outfile: "dist/extension.js",
                    platform: "node", format: "cjs", target: "node20", external: ["vscode"] }),
  // Interfaz del chat: corre en el navegador del webview.
  esbuild.context({ ...comun, entryPoints: ["src/webview/main.ts"], outfile: "dist/webview.js",
                    platform: "browser", format: "iife", target: "es2022" })
]);

if (watch) await Promise.all(contextos.map(c => c.watch()));
else { await Promise.all(contextos.map(c => c.rebuild())); await Promise.all(contextos.map(c => c.dispose())); }
