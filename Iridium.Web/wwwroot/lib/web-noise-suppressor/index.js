// src/noiseGate/workletUtil.ts
const id$3 = "@sapphi-red/web-noise-suppressor/noise-gate";
// src/noiseGate/workletNode.ts
var NoiseGateWorkletNode = class extends AudioWorkletNode {
	constructor(context, { openThreshold, closeThreshold = openThreshold, holdMs, maxChannels }) {
		super(context, id$3, { processorOptions: {
			openThreshold,
			closeThreshold,
			holdMs,
			maxChannels
		} });
	}
};
// src/utils/fetchArrayBuffer.ts
const fetchArrayBuffer = async (url, init) => {
	return await (await fetch(url, init)).arrayBuffer();
};
// src/gtcrn/load.ts
const loadGtcrn = async ({ url }, init) => {
	return await fetchArrayBuffer(url, init);
};
// src/gtcrn/workletUtil.ts
const id$2 = "@sapphi-red/web-noise-suppressor/gtcrn";
// src/gtcrn/workletNode.ts
var GtcrnWorkletNode = class extends AudioWorkletNode {
	constructor(context, { maxChannels, wasmBinary }) {
		super(context, id$2, { processorOptions: {
			maxChannels,
			wasmBinary
		} });
	}
	destroy() {
		this.port.postMessage("destroy");
	}
};
// node_modules/.pnpm/wasm-feature-detect@1.9.0/node_modules/wasm-feature-detect/dist/esm/index.js
const simd = async () => WebAssembly.validate(new Uint8Array([
	0,
	97,
	115,
	109,
	1,
	0,
	0,
	0,
	1,
	5,
	1,
	96,
	0,
	1,
	123,
	3,
	2,
	1,
	0,
	10,
	10,
	1,
	8,
	0,
	65,
	0,
	253,
	15,
	253,
	98,
	11
]));
// src/rnnoise/load.ts
const loadRnnoise = async ({ url, simdUrl }, init) => {
	const loadUrl = await simd() ? simdUrl : url;
	return await fetchArrayBuffer(loadUrl, init);
};
// src/rnnoise/workletUtil.ts
const id$1 = "@sapphi-red/web-noise-suppressor/rnnoise";
// src/rnnoise/workletNode.ts
/**
* Assumes sample rate to be 48kHz.
*/
var RnnoiseWorkletNode = class extends AudioWorkletNode {
	constructor(context, { maxChannels, wasmBinary }) {
		super(context, id$1, { processorOptions: {
			maxChannels,
			wasmBinary
		} });
	}
	destroy() {
		this.port.postMessage("destroy");
	}
};
// src/speex/load.ts
const loadSpeex = async ({ url }, init) => {
	return await fetchArrayBuffer(url, init);
};
// src/speex/workletUtil.ts
const id = "@sapphi-red/web-noise-suppressor/speex";
// src/speex/workletNode.ts
var SpeexWorkletNode = class extends AudioWorkletNode {
	constructor(context, { maxChannels, wasmBinary }) {
		super(context, id, { processorOptions: {
			maxChannels,
			wasmBinary
		} });
	}
	destroy() {
		this.port.postMessage("destroy");
	}
};
export { GtcrnWorkletNode, NoiseGateWorkletNode, RnnoiseWorkletNode, SpeexWorkletNode, loadGtcrn, loadRnnoise, loadSpeex };

