export function isDisplayCaptureSupported() {
    return typeof navigator?.mediaDevices?.getDisplayMedia === "function";
}
