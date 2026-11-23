let speechSynthesis = window.speechSynthesis;
let currentUtterance = null;
let dotNetRef = null;
let resumeTimer = null;
let currentAudio = null;

window.speakText = function(text, dotNetObjRef, seq) {
    if (speechSynthesis) {
        speechSynthesis.cancel();
    }
    if (resumeTimer) {
        clearInterval(resumeTimer);
        resumeTimer = null;
    }
    currentUtterance = null;

    if (!speechSynthesis) {
        console.warn('浏览器不支持语音合成');
        notifyFailed(dotNetObjRef, seq, '浏览器不支持语音合成');
        return false;
    }

    // 无可用语音包（Windows 未安装中文语音）时 speak() 会抛异常打崩 Blazor circuit，提前拦截
    if (!speechSynthesis.getVoices || speechSynthesis.getVoices().length === 0) {
        console.warn('当前环境没有可用的语音包');
        notifyFailed(dotNetObjRef, seq, '当前环境不支持语音合成（未安装语音包）');
        return false;
    }

    if (dotNetObjRef) {
        // 不主动 dispose 旧 ref：旧 utterance 的 onend/onerror 回调可能仍在途，
        // dispose 会导致 “There is no tracked object” 异常打崩回调；生命周期交给 Blazor 组件 Dispose。
        dotNetRef = dotNetObjRef;
    }

    const utterance = new SpeechSynthesisUtterance(text);
    utterance.lang = 'zh-CN';
    utterance.rate = 1.0;
    utterance.pitch = 1.0;

    const mySeq = seq;

    utterance.onstart = function() {
        resumeTimer = setInterval(function() {
            if (speechSynthesis && speechSynthesis.speaking && speechSynthesis.paused) {
                speechSynthesis.resume();
            }
        }, 10000);
    };

    utterance.onend = function() {
        currentUtterance = null;
        if (resumeTimer) {
            clearInterval(resumeTimer);
            resumeTimer = null;
        }
        if (dotNetRef) {
            setTimeout(function() {
                try {
                    dotNetRef.invokeMethodAsync('OnSpeechEnded', mySeq);
                } catch (e) {
                    console.warn('通知播放结束失败:', e);
                }
            }, 100);
        }
    };

    utterance.onerror = function(e) {
        currentUtterance = null;
        if (resumeTimer) {
            clearInterval(resumeTimer);
            resumeTimer = null;
        }
        if (e && e.error === 'canceled') return;
        if (dotNetRef) {
            setTimeout(function() {
                try {
                    dotNetRef.invokeMethodAsync('OnSpeechEnded', mySeq);
                } catch (ex) {
                    console.warn('通知播放错误失败:', ex);
                }
            }, 100);
        }
    };

    currentUtterance = utterance;
    try {
        speechSynthesis.speak(utterance);
    } catch (e) {
        console.warn('语音合成异常:', e);
        notifyFailed(dotNetObjRef, seq, '语音合成异常：' + (e && e.message ? e.message : e));
        return false;
    }
    return true;
};

// 通知 Blazor 播放失败（不抛异常，避免打崩 circuit）
function notifyFailed(ref, seq, message) {
    if (!ref) return;
    setTimeout(function() {
        try {
            ref.invokeMethodAsync('OnSpeechFailed', seq, message);
        } catch (e) {
            console.warn('通知播放失败回调异常:', e);
        }
    }, 50);
}

window.stopSpeaking = function() {
    if (currentAudio) {
        currentAudio.pause();
        currentAudio = null;
    }
    if (speechSynthesis) {
        speechSynthesis.cancel();
    }
    if (resumeTimer) {
        clearInterval(resumeTimer);
        resumeTimer = null;
    }
    currentUtterance = null;
};

window.isSpeaking = function() {
    return (speechSynthesis && speechSynthesis.speaking) || (currentAudio && !currentAudio.paused);
};

window.playAudioFromBase64 = function(base64Data, dotNetObjRef, seq) {
    if (currentAudio) {
        currentAudio.pause();
        currentAudio = null;
    }
    if (speechSynthesis) {
        speechSynthesis.cancel();
    }

    if (!base64Data) {
        notifyFailed(dotNetObjRef, seq, '音频数据为空');
        return false;
    }

    if (dotNetObjRef) {
        dotNetRef = dotNetObjRef;
    }

    try {
        var byteChars = atob(base64Data);
        var byteNumbers = new Array(byteChars.length);
        for (var i = 0; i < byteChars.length; i++) {
            byteNumbers[i] = byteChars.charCodeAt(i);
        }
        var byteArray = new Uint8Array(byteNumbers);
        var blob = new Blob([byteArray], { type: 'audio/wav' });
        var url = URL.createObjectURL(blob);

        var audio = new Audio(url);
        var mySeq = seq;

        audio.onended = function() {
            URL.revokeObjectURL(url);
            currentAudio = null;
            if (dotNetRef) {
                setTimeout(function() {
                    try {
                        dotNetRef.invokeMethodAsync('OnSpeechEnded', mySeq);
                    } catch (e) {
                        console.warn('通知播放结束失败:', e);
                    }
                }, 100);
            }
        };

        audio.onerror = function(e) {
            URL.revokeObjectURL(url);
            currentAudio = null;
            if (dotNetRef) {
                setTimeout(function() {
                    try {
                        dotNetRef.invokeMethodAsync('OnSpeechFailed', mySeq, '音频播放错误');
                    } catch (ex) {
                        console.warn('通知播放错误失败:', ex);
                    }
                }, 100);
            }
        };

        currentAudio = audio;
        audio.play().catch(function(e) {
            URL.revokeObjectURL(url);
            currentAudio = null;
            notifyFailed(dotNetObjRef, seq, '音频播放失败: ' + (e.message || e));
        });

        return true;
    } catch (e) {
        console.warn('playAudioFromBase64 异常:', e);
        notifyFailed(dotNetObjRef, seq, '音频解码异常: ' + (e.message || e));
        return false;
    }
};
