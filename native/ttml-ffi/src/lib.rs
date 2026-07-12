use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::slice;

use serde::Serialize;
use ttml_processor::amll::to_amll_lyrics;
use ttml_processor::parse_ttml;

pub const ABI_VERSION: u32 = 1;

#[repr(C)]
pub struct FfiBuffer {
    pub ptr: *mut u8,
    pub len: usize,
    pub status: i32,
}

#[derive(Serialize)]
struct AmllWordDto {
    #[serde(rename = "startTime")]
    start_time: i64,
    #[serde(rename = "endTime")]
    end_time: i64,
    word: String,
}

#[derive(Serialize)]
struct AmllLineDto {
    #[serde(rename = "startTime")]
    start_time: i64,
    #[serde(rename = "endTime")]
    end_time: i64,
    words: Vec<AmllWordDto>,
    #[serde(rename = "translatedLyric")]
    translated_lyric: String,
    #[serde(rename = "romanLyric")]
    roman_lyric: String,
    #[serde(rename = "isBackground")]
    is_background: bool,
    #[serde(rename = "isDuet")]
    is_duet: bool,
}

#[derive(Serialize)]
struct AmllDocumentDto {
    lines: Vec<AmllLineDto>,
}

#[derive(Serialize)]
struct ErrorDto {
    error: String,
    category: String,
}

fn make_buffer(bytes: Vec<u8>, status: i32) -> FfiBuffer {
    let len = bytes.len();
    let mut boxed = bytes.into_boxed_slice();
    let ptr = boxed.as_mut_ptr();
    std::mem::forget(boxed);
    FfiBuffer { ptr, len, status }
}

fn error_buffer(message: impl Into<String>, category: &str, status: i32) -> FfiBuffer {
    let payload = ErrorDto {
        error: message.into(),
        category: category.to_string(),
    };
    match serde_json::to_vec(&payload) {
        Ok(bytes) => make_buffer(bytes, status),
        Err(_) => FfiBuffer {
            ptr: ptr::null_mut(),
            len: 0,
            status,
        },
    }
}

fn parse_inner(input: &[u8]) -> Result<Vec<u8>, String> {
    let text = std::str::from_utf8(input).map_err(|err| format!("invalid utf-8: {err}"))?;
    let parsed = parse_ttml(text).map_err(|err| format!("parse error: {err}"))?;
    let amll = to_amll_lyrics(parsed, None);

    let lines = amll
        .lines
        .into_iter()
        .map(|line| {
            let words = line
                .words
                .into_iter()
                .map(|word| AmllWordDto {
                    start_time: word.start_time as i64,
                    end_time: word.end_time as i64,
                    word: word.word.to_string(),
                })
                .collect::<Vec<_>>();
            AmllLineDto {
                start_time: line.start_time as i64,
                end_time: line.end_time as i64,
                words,
                translated_lyric: line.translated_lyric,
                roman_lyric: line.roman_lyric,
                is_background: line.is_bg,
                is_duet: line.is_duet,
            }
        })
        .collect::<Vec<_>>();

    let document = AmllDocumentDto { lines };
    serde_json::to_vec(&document).map_err(|err| format!("serialize error: {err}"))
}

#[no_mangle]
pub extern "C" fn mediaisland_ttml_abi_version() -> u32 {
    ABI_VERSION
}

#[no_mangle]
pub extern "C" fn mediaisland_ttml_parse_to_amll_json(
    input_ptr: *const u8,
    input_len: usize,
    _options_ptr: *const u8,
    _options_len: usize,
) -> FfiBuffer {
    if input_ptr.is_null() && input_len != 0 {
        return error_buffer("null input pointer", "invalid_argument", 1);
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let input = if input_len == 0 {
            &[][..]
        } else {
            unsafe { slice::from_raw_parts(input_ptr, input_len) }
        };
        parse_inner(input)
    }));

    match result {
        Ok(Ok(bytes)) => make_buffer(bytes, 0),
        Ok(Err(message)) => error_buffer(message, "parse_error", 2),
        Err(_) => error_buffer("native panic", "panic", 3),
    }
}

#[no_mangle]
pub extern "C" fn mediaisland_ttml_free(ptr: *mut u8, len: usize) {
    if ptr.is_null() || len == 0 {
        return;
    }
    unsafe {
        let _ = Vec::from_raw_parts(ptr, len, len);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SAMPLE_TTML: &str = r#"
        <tt xmlns="http://www.w3.org/ns/ttml" itunes:timing="word">
          <body>
            <div>
              <p begin="5.000" end="10.000">
                <span begin="5.100" end="5.500">Hello </span>
                <span begin="5.600" end="6.000">world</span>
              </p>
            </div>
          </body>
        </tt>
    "#;

    #[test]
    fn parse_inner_returns_stable_amll_json_shape() {
        let bytes = parse_inner(SAMPLE_TTML.as_bytes()).expect("sample TTML should parse");
        let json: serde_json::Value = serde_json::from_slice(&bytes).expect("output should be JSON");

        let lines = json["lines"].as_array().expect("lines should be an array");
        assert_eq!(lines.len(), 1);
        assert_eq!(lines[0]["startTime"], 5000);
        assert_eq!(lines[0]["words"][0]["word"], "Hello ");
    }

    #[test]
    fn ffi_returns_bounded_error_for_invalid_utf8() {
        let input = [0xff_u8];
        let buffer = mediaisland_ttml_parse_to_amll_json(
            input.as_ptr(),
            input.len(),
            ptr::null(),
            0,
        );

        assert_ne!(buffer.status, 0);
        assert!(!buffer.ptr.is_null());
        assert!(buffer.len > 0);
        mediaisland_ttml_free(buffer.ptr, buffer.len);
    }

    #[test]
    fn ffi_rejects_null_pointer_with_nonzero_length() {
        let buffer = mediaisland_ttml_parse_to_amll_json(ptr::null(), 1, ptr::null(), 0);

        assert_eq!(buffer.status, 1);
        assert!(!buffer.ptr.is_null());
        mediaisland_ttml_free(buffer.ptr, buffer.len);
    }
}
