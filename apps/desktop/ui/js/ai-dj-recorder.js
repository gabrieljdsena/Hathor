/* Microphone vibe recording for Vohithe (6 s VAD windows, WAV base64). */
(function () {
  'use strict';

  var VAD_THRESHOLD = 0.008;
  var WINDOW_SECONDS = 6;
  var active = false;
  var context = null;
  var stream = null;
  var processor = null;
  var source = null;
  var sink = null;
  var onText = null;
  var onState = null;
  var onError = null;

  function bridge() {
    return (window.pywebview && window.pywebview.api) ? window.pywebview.api : null;
  }

  function blockRms(block) {
    var sum = 0;
    for (var i = 0; i < block.length; i += 1) sum += block[i] * block[i];
    return Math.sqrt(sum / block.length);
  }

  function encodeWav(samples, sampleRate) {
    var dataSize = samples.length * 2;
    var buffer = new ArrayBuffer(44 + dataSize);
    var view = new DataView(buffer);
    function write(offset, text) {
      for (var i = 0; i < text.length; i += 1) view.setUint8(offset + i, text.charCodeAt(i));
    }
    write(0, 'RIFF');
    view.setUint32(4, 36 + dataSize, true);
    write(8, 'WAVE');
    write(12, 'fmt ');
    view.setUint32(16, 16, true);
    view.setUint16(20, 1, true);
    view.setUint16(22, 1, true);
    view.setUint32(24, sampleRate, true);
    view.setUint32(28, sampleRate * 2, true);
    view.setUint16(32, 2, true);
    view.setUint16(34, 16, true);
    write(36, 'data');
    view.setUint32(40, dataSize, true);
    for (var i = 0; i < samples.length; i += 1) {
      var clamped = Math.max(-1, Math.min(1, samples[i]));
      view.setInt16(44 + i * 2, clamped * 0x7fff, true);
    }
    var bytes = new Uint8Array(buffer);
    var binary = '';
    for (var j = 0; j < bytes.length; j += 1) binary += String.fromCharCode(bytes[j]);
    return btoa(binary);
  }

  function notifyState(recording, error) {
    if (onState) {
      try { onState(recording, error || null); } catch (e) {}
    }
  }

  function stop() {
    active = false;
    try { processor && processor.disconnect(); } catch (e) {}
    try { source && source.disconnect(); } catch (e) {}
    try { sink && sink.disconnect(); } catch (e) {}
    processor = null;
    source = null;
    sink = null;
    if (stream) stream.getTracks().forEach(function (track) { track.stop(); });
    stream = null;
    if (context) {
      try { context.close(); } catch (e) {}
    }
    context = null;
    notifyState(false, null);
  }

  async function start(textCallback, stateCallback, errorCallback) {
    onText = textCallback || null;
    onState = stateCallback || null;
    onError = errorCallback || null;
    if (active) {
      stop();
      return;
    }
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      if (onError) onError('Microphone is not available in this window.');
      return;
    }
    try {
      var media = await navigator.mediaDevices.getUserMedia({ audio: true });
      var audioContext = new (window.AudioContext || window.webkitAudioContext)();
      var micSource = audioContext.createMediaStreamSource(media);
      var proc = audioContext.createScriptProcessor(4096, 1, 1);
      var silent = audioContext.createGain();
      silent.gain.value = 0;
      micSource.connect(proc);
      proc.connect(silent);
      silent.connect(audioContext.destination);
      stream = media;
      context = audioContext;
      source = micSource;
      processor = proc;
      sink = silent;
      active = true;
      notifyState(true, null);
      var sampleRate = audioContext.sampleRate;
      var chunks = [];
      var total = 0;
      var peak = 0;
      proc.onaudioprocess = function (event) {
        if (!active) return;
        var chunk = new Float32Array(event.inputBuffer.getChannelData(0));
        peak = Math.max(peak, blockRms(chunk));
        chunks.push(chunk);
        total += chunk.length;
        if (total < sampleRate * WINDOW_SECONDS) return;
        var merged = new Float32Array(total);
        var offset = 0;
        chunks.forEach(function (part) {
          merged.set(part, offset);
          offset += part.length;
        });
        var hadSpeech = peak >= VAD_THRESHOLD;
        chunks = [];
        total = 0;
        peak = 0;
        if (!hadSpeech) return;
        var api = bridge();
        if (!api || !api.ai_dj_transcribe) return;
        api.ai_dj_transcribe(encodeWav(merged, sampleRate), navigator.language || 'en-US')
          .then(function (result) {
            if (result && result.ok && result.text && onText) onText(result.text.trim());
            else if (result && result.message && onError) onError(result.message);
          })
          .catch(function () {
            if (onError) onError('Could not transcribe that. Try typing instead.');
          });
      };
    } catch (error) {
      stop();
      if (onError) onError('Microphone permission was denied.');
    }
  }

  window.AiDjRecorder = {
    start: start,
    stop: stop,
    toggle: start,
    isRecording: function () { return active; }
  };
})();
