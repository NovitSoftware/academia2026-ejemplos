// Lleva el chat al último mensaje cuando llega uno nuevo.
window.mesaya = {
    alFinal: (el) => { if (el) el.scrollTop = el.scrollHeight; }
};
