// Wraps the browser's navigator.geolocation.getCurrentPosition callback API in a Promise so it
// can be awaited from .NET via JS interop.
export function getCurrentPosition() {
    return new Promise((resolve, reject) => {
        if (!navigator.geolocation) {
            reject("Geolocation is not supported by this browser.");
            return;
        }

        navigator.geolocation.getCurrentPosition(
            position => resolve({
                latitude: position.coords.latitude,
                longitude: position.coords.longitude
            }),
            error => reject(error.message || "Failed to get the user's location."),
            { enableHighAccuracy: false, timeout: 10000, maximumAge: 60000 });
    });
}
