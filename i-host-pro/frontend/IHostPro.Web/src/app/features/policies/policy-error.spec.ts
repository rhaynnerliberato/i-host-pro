import { classifyPolicyActionError } from './policy-error';

describe('classifyPolicyActionError', () => {
  it('extracts status and title from a deserialized ProblemDetails-shaped object', () => {
    expect(classifyPolicyActionError({ status: 409, title: 'version_conflict' })).toEqual({
      status: 409,
      title: 'version_conflict',
      codes: [],
    });
  });

  it('extracts codes for the generic validation_error fallback', () => {
    expect(classifyPolicyActionError({ status: 400, title: 'validation_error', codes: ['ScopeType.Required'] })).toEqual({
      status: 400,
      title: 'validation_error',
      codes: ['ScopeType.Required'],
    });
  });

  it('recognizes an ApiException-like object (Error subclass with status/title fields)', () => {
    class ApiExceptionLike extends Error {
      status: number;
      title: string;
      constructor(status: number, title: string) {
        super(title);
        this.status = status;
        this.title = title;
      }
    }
    expect(classifyPolicyActionError(new ApiExceptionLike(403, 'forbidden'))).toEqual({
      status: 403,
      title: 'forbidden',
      codes: [],
    });
  });

  it('ignores a non-array codes field instead of throwing', () => {
    expect(classifyPolicyActionError({ status: 400, title: 'validation_error', codes: 'not-an-array' })).toEqual({
      status: 400,
      title: 'validation_error',
      codes: [],
    });
  });

  it('filters out non-string entries from the codes array', () => {
    expect(classifyPolicyActionError({ status: 400, title: 'validation_error', codes: ['VALID', 123, null] })).toEqual({
      status: 400,
      title: 'validation_error',
      codes: ['VALID'],
    });
  });

  it('treats an object with no status/title fields as unclassified', () => {
    expect(classifyPolicyActionError({ message: 'boom' })).toEqual({ status: undefined, title: undefined, codes: [] });
  });

  it('never throws for null/undefined/string/number values and treats them as unclassified', () => {
    expect(classifyPolicyActionError(null)).toEqual({ status: undefined, title: undefined, codes: [] });
    expect(classifyPolicyActionError(undefined)).toEqual({ status: undefined, title: undefined, codes: [] });
    expect(classifyPolicyActionError('some string')).toEqual({ status: undefined, title: undefined, codes: [] });
    expect(classifyPolicyActionError(42)).toEqual({ status: undefined, title: undefined, codes: [] });
  });
});
