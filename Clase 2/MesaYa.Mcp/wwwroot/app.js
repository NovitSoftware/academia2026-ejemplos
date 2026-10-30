window.mcpLab = {
    // Lleva al final los contenedores con scroll indicados (chat, pasos, logs, diagrama).
    abajo: (...ids) => {
        for (const id of ids) {
            const el = document.getElementById(id);
            if (el) el.scrollTop = el.scrollHeight;
        }
    }
};
