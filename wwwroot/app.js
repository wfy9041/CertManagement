window.certManagement = {
    observeScroll(element, callback) {
        const onScroll = () => {
            if (element.scrollTop + element.clientHeight >= element.scrollHeight - 48) {
                callback.invokeMethodAsync('LoadMoreAsync');
            }
        };
        element.__certManagementScrollHandler = onScroll;
        element.addEventListener('scroll', onScroll, { passive: true });
    },
    unobserveScroll(element) {
        if (element && element.__certManagementScrollHandler) {
            element.removeEventListener('scroll', element.__certManagementScrollHandler);
            delete element.__certManagementScrollHandler;
        }
    }
};