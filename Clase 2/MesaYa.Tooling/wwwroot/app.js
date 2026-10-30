window.mesaYaScroll = () => {
    for (const id of ['chat-scroll', 'activity-scroll']) {
        const element = document.getElementById(id);
        if (element) element.scrollTop = element.scrollHeight;
    }
};
