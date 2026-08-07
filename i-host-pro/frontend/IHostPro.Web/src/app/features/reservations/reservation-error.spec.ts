import { classifyReservationError } from './reservation-error';

describe('classifyReservationError', () => {
  it('extracts status and codes from a deserialized 400 ProblemDetails-shaped object', () => {
    expect(classifyReservationError({ status: 400, title: 'validation_failed', codes: ['Reservations.PropertyCapacityExceeded'] })).toEqual({
      status: 400,
      codes: ['Reservations.PropertyCapacityExceeded'],
    });
  });

  it('extracts status with an empty codes array for a 404 (never a code-specific reason)', () => {
    expect(classifyReservationError({ status: 404, title: 'not_found' })).toEqual({ status: 404, codes: [] });
  });

  it('extracts status with an empty codes array for a 409 (never a code-specific reason)', () => {
    expect(classifyReservationError({ status: 409, title: 'conflict' })).toEqual({ status: 409, codes: [] });
  });

  it('recognizes an ApiException-like object (Error subclass with a status field)', () => {
    class ApiExceptionLike extends Error {
      status: number;
      constructor(status: number) {
        super('Conflict');
        this.status = status;
      }
    }
    expect(classifyReservationError(new ApiExceptionLike(409))).toEqual({ status: 409, codes: [] });
  });

  it('ignores a non-array codes field instead of throwing', () => {
    expect(classifyReservationError({ status: 400, codes: 'not-an-array' })).toEqual({ status: 400, codes: [] });
  });

  it('filters out non-string entries from the codes array', () => {
    expect(classifyReservationError({ status: 400, codes: ['VALID_CODE', 123, null] })).toEqual({
      status: 400,
      codes: ['VALID_CODE'],
    });
  });

  it('treats an object with no status field as having an undefined status', () => {
    expect(classifyReservationError({ message: 'boom' })).toEqual({ status: undefined, codes: [] });
  });

  it('never throws for null/undefined/string/number values and treats them as unclassified', () => {
    expect(classifyReservationError(null)).toEqual({ status: undefined, codes: [] });
    expect(classifyReservationError(undefined)).toEqual({ status: undefined, codes: [] });
    expect(classifyReservationError('some string')).toEqual({ status: undefined, codes: [] });
    expect(classifyReservationError(42)).toEqual({ status: undefined, codes: [] });
  });
});
