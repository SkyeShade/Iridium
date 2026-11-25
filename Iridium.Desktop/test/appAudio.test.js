const test = require('node:test');
const assert = require('node:assert/strict');
const { helperArguments, windowHandleFromSourceId } = require('../src/appAudio');

test('window sources capture that window\'s program', () => {
    assert.equal(windowHandleFromSourceId('window:132458:0'), '132458');
    assert.deepEqual(helperArguments('window:132458:0', 4242), ['--hwnd', '132458']);
});

test('screen sources capture everything except Iridium itself', () => {
    assert.equal(windowHandleFromSourceId('screen:0:0'), null);
    assert.deepEqual(helperArguments('screen:0:0', 4242), ['--pid', '4242', '--exclude']);
});
