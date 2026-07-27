// Blazor's declarative @onkeydown:preventDefault can't conditionally target just the Enter key
// (it would block all keydown-driven text entry), so this attaches a plain keydown listener:
// Enter alone submits (preventing the textarea's default newline), Shift+Enter still inserts one.
export function attachEnterToSend(textAreaElement, dotNetRef) {
    function onKeyDown(e) {
        if (e.key === "Enter" && !e.shiftKey) {
            e.preventDefault();
            dotNetRef.invokeMethodAsync("SubmitFromJsAsync");
        }
    }

    textAreaElement.addEventListener("keydown", onKeyDown);

    return {
        dispose: () => textAreaElement.removeEventListener("keydown", onKeyDown)
    };
}
