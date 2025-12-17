use crc32fast::Hasher;
use super::header::{ MessageType, FixedHeader, HeaderError, HEADER_SIZE };

#[derive(Debug)]
pub struct NetworkPacket {
    pub header: FixedHeader,
    pub payload: Vec<u8>,
}

#[derive(Debug, thiserror::Error)]
pub enum PacketError {
    #[error("Header error: {0}")] HeaderError(#[from] HeaderError),
    #[error("Payload length mismatch: expected {expected}, got {got}")] PayloadLengthMismatch {
        expected: u32,
        got: usize,
    },
    #[error(
        "Checksum mismatch: expected {expected:#x}, calculated {calculated:#x}"
    )] ChecksumMismatch {
        expected: u32,
        calculated: u32,
    },
}

impl NetworkPacket {
    /// Creates a new NetworkPacket with the given message type, request ID, and payload for sending (calculates CRC automatically).
    pub fn new(message_type: MessageType, request_id: u32, payload: Vec<u8>) -> Self {
        let header = FixedHeader::create(message_type, request_id, &payload);

        Self {
            header,
            payload,
        }
    }

    /// Serializes the entire packet (Header + Payload) to a vector of bytes.
    /// Ready to be sent over the wire (QUIC stream).
    pub fn to_bytes(&self) -> Vec<u8> {
        let mut buffer = Vec::with_capacity(HEADER_SIZE + self.payload.len());
        buffer.extend_from_slice(&self.header.to_bytes());
        buffer.extend_from_slice(&self.payload);
        buffer
    }

    /// Parses a packet from raw bytes.
    /// Validates the checksum for CRC32.
    pub fn from_bytes(data: &[u8]) -> Result<Self, PacketError> {
        if data.len() < HEADER_SIZE {
            return Err(PacketError::HeaderError(HeaderError::BufferTooSmall.into()));
        }

        // 1. Parse Header
        let header_bytes = &data[0..HEADER_SIZE];
        let header = FixedHeader::from_bytes(header_bytes)?;

        // 2. Validate payload length
        let payload_len = header.payload_length as usize;
        let expected_total_len = HEADER_SIZE + payload_len;

        if data.len() < expected_total_len {
            return Err(PacketError::PayloadLengthMismatch {
                expected: payload_len as u32,
                got: data.len() - HEADER_SIZE,
            });
        }

        // 3. Extract Payload
        let payload = data[HEADER_SIZE..expected_total_len].to_vec();

        // 4. Verify checksum
        let mut hasher = Hasher::new();
        hasher.update(&payload);
        let calculated_crc = hasher.finalize();

        if calculated_crc != header.checksum {
            return Err(PacketError::ChecksumMismatch {
                expected: header.checksum,
                calculated: calculated_crc,
            });
        }

        Ok(Self {
            header,
            payload,
        })
    }
}
